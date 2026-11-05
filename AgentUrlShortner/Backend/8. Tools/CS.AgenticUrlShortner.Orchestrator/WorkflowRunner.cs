using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CS.AgenticUrlShortner.Orchestrator
{
    internal sealed class WorkflowRunner
    {
        private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(20);
        private static readonly Regex SecretPattern = new(
            "\"[^\"]*(password|secret|apikey|api_key|connectionstring|token)[^\"]*\"\\s*:\\s*\"[^\"]+\"",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly string _repositoryRoot;
        private readonly string _backendRoot;
        private readonly string _solutionPath;
        private readonly string _releaseReadinessProject;
        private readonly WorkflowStore _store;
        private readonly GitWorkspace _git;
        private readonly IReadOnlyList<StageDefinition> _definitions = WorkflowPlan.Create();

        public WorkflowRunner(string repositoryRoot)
        {
            _repositoryRoot = repositoryRoot;
            _backendRoot = Path.Combine(repositoryRoot, "AgentUrlShortner", "Backend");
            _solutionPath = Path.Combine(_backendRoot, "CS.AgenticUrlShortner.sln");
            _releaseReadinessProject = Path.Combine(
                _backendRoot, "8. Tools", "CS.AgenticUrlShortner.ReleaseReadiness",
                "CS.AgenticUrlShortner.ReleaseReadiness.csproj");
            _store = new WorkflowStore(repositoryRoot);
            _git = new GitWorkspace(repositoryRoot);
        }

        public async Task<WorkflowRun> CreateRunAsync(string task, CancellationToken cancellationToken)
        {
            WorkflowPolicy.Validate(task);

            var runId = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
            var now = DateTimeOffset.UtcNow;
            var isClean = await _git.IsCleanAsync(cancellationToken).ConfigureAwait(false);
            var run = new WorkflowRun
            {
                RunId = runId,
                Task = task.Trim(),
                PlanFingerprint = WorkflowPlan.Fingerprint(_definitions),
                Status = "Pending",
                StartedAtUtc = now,
                UpdatedAtUtc = now,
                BaselineCommit = await _git.GetHeadAsync(cancellationToken).ConfigureAwait(false),
                RollbackAvailable = isClean,
                RollbackUnavailableReason = isClean
                    ? null
                    : "The worktree was not clean at run start; guarded rollback is disabled to preserve pre-existing changes.",
                Stages = _definitions.Select(definition => new StageRun
                {
                    StageId = definition.Id,
                    Status = StageStatus.Pending
                }).ToList()
            };

            await _store.SaveAsync(run, cancellationToken).ConfigureAwait(false);
            await AuditAsync(run, "run.created", null, "orchestrator", "New governed SDLC run created.", cancellationToken)
                .ConfigureAwait(false);
            return run;
        }

        public async Task<WorkflowRun> LoadRunAsync(string runId, CancellationToken cancellationToken) =>
            await _store.LoadAsync(runId, cancellationToken).ConfigureAwait(false);

        public async Task RecordRollbackAsync(WorkflowRun run, CancellationToken cancellationToken)
        {
            run.Metrics.EndToEndMilliseconds = (long)(DateTimeOffset.UtcNow - run.StartedAtUtc).TotalMilliseconds;
            await WriteSummaryAsync(run, cancellationToken).ConfigureAwait(false);
            await FinalizeSummaryFingerprintAsync(run, cancellationToken).ConfigureAwait(false);
            await AuditAsync(run, "run.rolled-back", null, "human",
                $"Guarded rollback restored the recorded baseline {run.BaselineCommit}.", cancellationToken)
                .ConfigureAwait(false);
        }

        public IDisposable AcquireWorkspaceLock() => RunLock.Acquire(_store, "workspace");

        public async Task ApproveAsync(
            WorkflowRun run, string stageId, string approver, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(approver))
                throw new ArgumentException("An approver name is required.", nameof(approver));

            var definition = _definitions.SingleOrDefault(stage => stage.Id == stageId);
            if (definition == null || definition.Kind != StageKind.Approval)
                throw new ArgumentException($"'{stageId}' is not an approval checkpoint.", nameof(stageId));

            var stage = GetStage(run, stageId);
            if (stage.Status != StageStatus.AwaitingApproval)
                throw new InvalidOperationException($"Stage '{stageId}' is not awaiting approval.");

            stage.ApprovedBy = approver.Trim();
            stage.Status = StageStatus.Pending;
            var approval = new ApprovalRecord
            {
                StageId = stageId,
                Approver = stage.ApprovedBy,
                ApprovedAtUtc = DateTimeOffset.UtcNow
            };
            run.Approvals.Add(approval);
            await AuditAsync(run, "approval.granted", stageId, stage.ApprovedBy,
                $"Human approval recorded for '{stageId}'.", cancellationToken).ConfigureAwait(false);
        }

        public async Task ReplanAsync(
            WorkflowRun run, string stageId, CancellationToken cancellationToken)
        {
            var target = _definitions.SingleOrDefault(stage => stage.Id == stageId);
            if (target == null)
                throw new ArgumentException($"Unknown stage '{stageId}'.", nameof(stageId));

            var affected = new HashSet<string>(StringComparer.Ordinal) { stageId };
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var definition in _definitions)
                {
                    if (!affected.Contains(definition.Id)
                        && definition.DependsOn.Any(affected.Contains))
                        changed |= affected.Add(definition.Id);
                }
            }

            foreach (var id in affected)
            {
                var stage = GetStage(run, id);
                stage.Status = StageStatus.Pending;
                stage.Attempts = 0;
                stage.UsedFallback = false;
                stage.StartedAtUtc = null;
                stage.FinishedAtUtc = null;
                stage.DurationMilliseconds = 0;
                stage.InputFingerprint = null;
                stage.OutputFingerprint = null;
                stage.ArtifactFingerprint = null;
                stage.LogFingerprint = null;
                stage.LastError = null;
                stage.ApprovedBy = null;
            }

            run.Approvals.RemoveAll(approval => affected.Contains(approval.StageId));
            run.Status = "Replanned";
            run.CompletedAtUtc = null;
            await AuditAsync(run, "workflow.replanned", stageId, "human",
                $"Stage '{stageId}' and all dependent stages were reset for a governed rerun.", cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<WorkflowRun> ExecuteAsync(WorkflowRun run, CancellationToken cancellationToken)
        {
            if (run.PlanFingerprint != WorkflowPlan.Fingerprint(_definitions))
                throw new InvalidOperationException(
                    $"Workflow plan changed since run '{run.RunId}' was created. Review the new plan and start a new run.");

            run.Status = "Running";
            run.UpdatedAtUtc = DateTimeOffset.UtcNow;
            foreach (var interrupted in run.Stages.Where(stage => stage.Status == StageStatus.Running))
            {
                interrupted.Status = StageStatus.Pending;
                interrupted.LastError = "Recovered an interrupted stage after a safe stop.";
                await AuditAsync(run, "stage.recovered", interrupted.StageId, "orchestrator",
                    interrupted.LastError, cancellationToken).ConfigureAwait(false);
            }
            await AuditAsync(run, "run.resumed", null, "orchestrator", "Workflow scheduler started.", cancellationToken)
                .ConfigureAwait(false);

            await ReconcileOutputsAsync(run, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                if (run.Stages.Any(stage => stage.Status is StageStatus.Failed or StageStatus.Blocked))
                {
                    MarkBlockedStages(run);
                    run.Status = "Failed";
                    break;
                }

                if (run.Stages.All(stage => stage.Status == StageStatus.Succeeded))
                {
                    run.Status = "Succeeded";
                    break;
                }

                var ready = WorkflowPlan.ReadyStages(_definitions, run);
                if (ready.Count == 0)
                {
                    var awaiting = run.Stages.FirstOrDefault(stage => stage.Status == StageStatus.AwaitingApproval);
                    if (awaiting != null)
                    {
                        run.Status = "AwaitingApproval";
                        break;
                    }

                    MarkBlockedStages(run);
                    run.Status = run.Stages.Any(stage => stage.Status == StageStatus.Blocked) ? "Blocked" : "Failed";
                    break;
                }

                var approvalGate = ready
                    .Select(stage => (_definitions.Single(definition => definition.Id == stage.StageId), stage))
                    .FirstOrDefault(pair => pair.Item1.Kind == StageKind.Approval && string.IsNullOrWhiteSpace(pair.stage.ApprovedBy));
                if (approvalGate.stage != null)
                {
                    approvalGate.stage.Status = StageStatus.AwaitingApproval;
                    approvalGate.stage.LastError = "Explicit human approval is required before proceeding.";
                    await AuditAsync(run, "approval.required", approvalGate.stage.StageId, "orchestrator",
                        approvalGate.stage.LastError, cancellationToken).ConfigureAwait(false);
                    run.Status = "AwaitingApproval";
                    break;
                }

                var batch = ready.Take(3).ToArray();
                foreach (var stage in batch)
                {
                    stage.Status = StageStatus.Running;
                    stage.StartedAtUtc = DateTimeOffset.UtcNow;
                    stage.LastError = null;
                    var definition = _definitions.Single(item => item.Id == stage.StageId);
                    stage.InputFingerprint = WorkflowPlan.InputFingerprint(definition, run);
                    if (definition.Kind != StageKind.Approval)
                    {
                        stage.Attempts++;
                        run.Metrics.StageAttempts++;
                    }
                }

                await SaveAsync(run, cancellationToken).ConfigureAwait(false);
                foreach (var stage in batch)
                    await AuditAsync(run, "stage.started", stage.StageId, "orchestrator",
                        $"Attempt {stage.Attempts} started.", cancellationToken).ConfigureAwait(false);

                StageOutcome[] outcomes;
                try
                {
                    outcomes = await Task.WhenAll(batch.Select(stage =>
                        ExecuteStageSafelyAsync(run, _definitions.Single(definition => definition.Id == stage.StageId),
                            stage, cancellationToken))).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    foreach (var stage in batch)
                        stage.Status = StageStatus.Pending;
                    break;
                }

                foreach (var outcome in outcomes)
                {
                    var stage = outcome.Stage;
                    stage.FinishedAtUtc = DateTimeOffset.UtcNow;
                    stage.DurationMilliseconds += outcome.DurationMilliseconds;
                    if (outcome.Success)
                    {
                        stage.Status = StageStatus.Succeeded;
                        stage.LastError = null;
                        run.Metrics.SuccessfulStages++;
                        await CaptureStageOutputAsync(run, stage, cancellationToken).ConfigureAwait(false);
                        await AuditAsync(run, "stage.succeeded", stage.StageId, "orchestrator",
                            outcome.Detail, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        stage.Status = StageStatus.Failed;
                        stage.LastError = outcome.Detail;
                        run.Metrics.FailedStages++;
                        if (_definitions.Single(definition => definition.Id == stage.StageId).Kind == StageKind.Agent)
                        {
                            stage.UsedFallback = true;
                            run.Metrics.FallbackCount++;
                            await WriteFallbackArtifactAsync(run, stage, outcome.Detail, cancellationToken)
                                .ConfigureAwait(false);
                            await AuditAsync(run, "stage.fallback", stage.StageId, "orchestrator",
                                "Bounded agent retries exhausted; recorded a human recovery handoff and stopped safely.",
                                cancellationToken).ConfigureAwait(false);
                        }

                        await AuditAsync(run, "stage.failed", stage.StageId, "orchestrator",
                            outcome.Detail, cancellationToken).ConfigureAwait(false);
                    }
                }

                await SaveAsync(run, cancellationToken).ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                run.Status = "Stopped";
                await AuditAsync(run, "run.safe-stopped", null, "orchestrator",
                    "Cancellation requested; no further stages will start.", CancellationToken.None).ConfigureAwait(false);
            }

            if (run.Status is "Succeeded" or "Failed" or "Blocked" or "Stopped")
            {
                run.CompletedAtUtc = DateTimeOffset.UtcNow;
                run.Metrics.EndToEndMilliseconds = (long)(run.CompletedAtUtc.Value - run.StartedAtUtc).TotalMilliseconds;
                await CaptureRollbackCheckpointAsync(run, CancellationToken.None).ConfigureAwait(false);
                await WriteSummaryAsync(run, CancellationToken.None).ConfigureAwait(false);
                await FinalizeSummaryFingerprintAsync(run, CancellationToken.None).ConfigureAwait(false);
                await AuditAsync(run, "run.completed", null, "orchestrator",
                    $"Run ended with status {run.Status}.", CancellationToken.None).ConfigureAwait(false);
            }

            run.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await SaveAsync(run, CancellationToken.None).ConfigureAwait(false);
            return run;
        }

        private async Task ReconcileOutputsAsync(WorkflowRun run, CancellationToken cancellationToken)
        {
            var changed = false;
            foreach (var definition in _definitions)
            {
                var stage = GetStage(run, definition.Id);
                if (stage.Status != StageStatus.Succeeded)
                    continue;

                var artifactHash = await HashArtifactAsync(stage.ArtifactPath, cancellationToken).ConfigureAwait(false);
                var logHash = await HashArtifactAsync(stage.LogPath, cancellationToken).ConfigureAwait(false);
                var currentHashes = await _git.HashFilesAsync(stage.ChangedFiles, cancellationToken).ConfigureAwait(false);
                var filesChanged = stage.ChangedFileHashes.Any(item =>
                    !currentHashes.TryGetValue(item.Key, out var currentHash) || currentHash != item.Value);
                if (artifactHash == stage.ArtifactFingerprint && logHash == stage.LogFingerprint && !filesChanged)
                    continue;

                stage.Status = StageStatus.Pending;
                stage.FinishedAtUtc = null;
                stage.OutputFingerprint = null;
                stage.ArtifactFingerprint = null;
                stage.LogFingerprint = null;
                stage.ApprovedBy = null;
                stage.LastError = "Output changed after completion; the stage and dependent work will be replanned.";
                changed = true;
                await AuditAsync(run, "stage.invalidated", stage.StageId, "orchestrator",
                    stage.LastError, cancellationToken).ConfigureAwait(false);
            }

            var priorStatuses = run.Stages.Select(stage => stage.Status).ToArray();
            WorkflowPlan.InvalidateStaleStages(_definitions, run);
            changed |= priorStatuses.Where((status, index) => status != run.Stages[index].Status).Any();
            if (changed)
            {
                run.Status = "Replanned";
                await AuditAsync(run, "workflow.replanned", null, "orchestrator",
                    "Detected changed stage outputs; stale downstream stages were invalidated.", cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private async Task<StageOutcome> ExecuteStageAsync(
            WorkflowRun run, StageDefinition definition, StageRun stage, CancellationToken cancellationToken)
        {
            var started = Stopwatch.StartNew();
            if (definition.Kind == StageKind.Approval)
                return StageOutcome.Succeeded(stage, 0, $"Approved by {stage.ApprovedBy}.");

            if (definition.Kind == StageKind.Agent)
            {
                var lastOutput = string.Empty;
                for (var attempt = 1; attempt <= definition.MaxAttempts; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var artifactPath = Path.Combine(_store.RunDirectory(run.RunId), "artifacts", definition.ArtifactName);
                    Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
                    if (File.Exists(artifactPath))
                        File.Delete(artifactPath);
                    var prompt = BuildAgentPrompt(run, definition, artifactPath, lastOutput);
                    var result = await RunCopilotAsync(prompt, cancellationToken).ConfigureAwait(false);
                    stage.LogPath = Path.Combine(_store.RunDirectory(run.RunId), "logs", $"{definition.Id}-attempt-{attempt}.log");
                    Directory.CreateDirectory(Path.GetDirectoryName(stage.LogPath)!);
                    await File.WriteAllTextAsync(stage.LogPath, result.Output, cancellationToken).ConfigureAwait(false);
                    lastOutput = Tail(result.Output, 12000);
                    run.Metrics.StageAttempts++;
                    if (attempt > 1)
                    {
                        run.Metrics.RetryCount++;
                        stage.Attempts++;
                        run.Metrics.StageAttempts++;
                    }

                    if (result.ExitCode == 0 && File.Exists(artifactPath) && new FileInfo(artifactPath).Length > 0)
                    {
                        var artifact = await File.ReadAllTextAsync(artifactPath, cancellationToken).ConfigureAwait(false);
                        var missingSections = WorkflowArtifactValidator.MissingSections(definition, artifact);
                        if (missingSections.Length == 0)
                        {
                            stage.ArtifactPath = artifactPath;
                            return StageOutcome.Succeeded(stage, started.ElapsedMilliseconds,
                                $"Agent completed '{definition.Name}' on attempt {attempt}; output gates passed.");
                        }

                        lastOutput = $"Artifact exit gate failed. Add these required headings: " +
                            string.Join(", ", missingSections.Select(section => $"## {section}"));
                    }
                    else
                    {
                        lastOutput = result.ExitCode == 0
                            ? $"Agent did not create required artifact '{definition.ArtifactName}'."
                            : $"Agent exited with code {result.ExitCode}.\n{Tail(result.Output, 12000)}";
                    }

                    if (attempt == definition.MaxAttempts)
                        return StageOutcome.Failed(stage, started.ElapsedMilliseconds, lastOutput);
                }
            }
            else
            {
                try
                {
                    var result = await ExecuteCommandStageAsync(run, definition, cancellationToken).ConfigureAwait(false);
                    stage.LogPath = result.LogPath;
                    return result.ExitCode == 0
                        ? StageOutcome.Succeeded(stage, started.ElapsedMilliseconds,
                            $"{definition.Name} completed successfully.")
                        : StageOutcome.Failed(stage, started.ElapsedMilliseconds,
                            $"{definition.Name} exited with code {result.ExitCode}.\n{Tail(result.Output, 12000)}");
                }
                catch (TimeoutException exception)
                {
                    return StageOutcome.Failed(stage, started.ElapsedMilliseconds, exception.Message);
                }
            }

            return StageOutcome.Failed(stage, started.ElapsedMilliseconds, "Stage ended without a result.");
        }

        private async Task<StageOutcome> ExecuteStageSafelyAsync(
            WorkflowRun run, StageDefinition definition, StageRun stage, CancellationToken cancellationToken)
        {
            try
            {
                return await ExecuteStageAsync(run, definition, stage, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidOperationException or TimeoutException)
            {
                return StageOutcome.Failed(stage, 0,
                    $"{definition.Name} failed with {exception.GetType().Name}: {exception.Message}");
            }
        }

        private string BuildAgentPrompt(
            WorkflowRun run, StageDefinition definition, string artifactPath, string previousAttempt)
        {
            var priorArtifacts = WorkflowPlan.UpstreamStageIds(_definitions, definition.Id)
                .Select(dependency => GetStage(run, dependency).ArtifactPath)
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
            var context = string.Join(Environment.NewLine, priorArtifacts.Select(path => $"- {path}"));
            var retry = string.IsNullOrWhiteSpace(previousAttempt)
                ? string.Empty
                : $"\nPrevious attempt output (fix the problem, do not repeat it):\n{previousAttempt}";
            var requiredHeadings = string.Join(Environment.NewLine,
                definition.RequiredArtifactSections.Select(section => $"## {section}"));
            return $"You are executing SDLC stage '{definition.Id}' ({definition.Name}).\n" +
                $"User task:\n{run.Task}\n\nStage instructions:\n{definition.Instructions}\n\n" +
                $"Repository root: {_repositoryRoot}\nRead these completed upstream artifacts:\n{context}\n\n" +
                $"Write this stage's deliverable to exactly: {artifactPath}\n" +
                $"Required artifact headings (all are exit gates):\n{requiredHeadings}\n" +
                "Follow AGENTS.md and AgentUrlShortner/docs/agent-instructions.md. Preserve unrelated work. " +
                "Never commit, merge, deploy, publish, or modify secrets. Report assumptions, decisions, risks, and files changed in the artifact." +
                retry;
        }

        private async Task<(int ExitCode, string Output)> RunCopilotAsync(
            string prompt, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo("copilot")
            {
                WorkingDirectory = _repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--autopilot");
            startInfo.ArgumentList.Add("--max-autopilot-continues");
            startInfo.ArgumentList.Add("10");
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add(prompt);
            return await RunProcessAsync(startInfo, cancellationToken).ConfigureAwait(false);
        }

        private async Task<StageOutcome> ExecuteCommandStageAsync(
            WorkflowRun run, StageDefinition definition, CancellationToken cancellationToken)
        {
            var started = Stopwatch.StartNew();
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = _repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            switch (definition.Kind)
            {
                case StageKind.Build:
                    AddArguments(startInfo, "build", _solutionPath, "-c", "Release", "-warnaserror", "-nologo");
                    break;
                case StageKind.Policy:
                    return await RunPolicyCheckAsync(run, definition, cancellationToken).ConfigureAwait(false);
                case StageKind.UnitTests:
                    AddArguments(startInfo, "test", _solutionPath, "-c", "Release", "--no-build", "--nologo",
                        "--filter", "FullyQualifiedName!~CS.AgenticUrlShortner.Tests.ApiTests");
                    break;
                case StageKind.ApiTests:
                    AddArguments(startInfo, "test", Path.Combine(_backendRoot, "9. Tests",
                        "CS.AgenticUrlShortner.Tests.ApiTests", "CS.AgenticUrlShortner.Tests.ApiTests.csproj"),
                        "-c", "Release", "--no-build", "--nologo");
                    break;
                case StageKind.Security:
                    return await RunSecurityChecksAsync(run, definition, cancellationToken).ConfigureAwait(false);
                case StageKind.ReleaseReadiness:
                {
                    var reportPath = Path.Combine(_store.RunDirectory(run.RunId), "artifacts", "release-readiness.md");
                    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                    GetStage(run, definition.Id).ArtifactPath = reportPath;
                    AddArguments(startInfo, "run", "--project", _releaseReadinessProject, "-c", "Release", "--",
                        "--root", _backendRoot, "--version", "0.1.0", "--approver",
                        GetStage(run, "release-approval").ApprovedBy,
                        "--output", reportPath);
                    break;
                }
                case StageKind.Summary:
                {
                    var summaryStage = GetStage(run, definition.Id);
                    summaryStage.ArtifactPath = Path.Combine(
                        _store.RunDirectory(run.RunId), "artifacts", "engineering-summary.md");
                    await WriteSummaryAsync(run, cancellationToken).ConfigureAwait(false);
                    return StageOutcome.Succeeded(summaryStage, 0, "Summary generated.");
                }
                default:
                    throw new InvalidOperationException($"Unsupported stage kind '{definition.Kind}'.");
            }

            var result = await RunProcessAsync(startInfo, cancellationToken).ConfigureAwait(false);
            var logPath = Path.Combine(_store.RunDirectory(run.RunId), "logs", $"{definition.Id}.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            await File.WriteAllTextAsync(logPath, result.Output, cancellationToken).ConfigureAwait(false);
            return StageOutcome.WithCommandResult(
                GetStage(run, definition.Id), result.ExitCode, result.Output, logPath, started.ElapsedMilliseconds);
        }

        private async Task<StageOutcome> RunSecurityChecksAsync(
            WorkflowRun run, StageDefinition definition, CancellationToken cancellationToken)
        {
            var started = Stopwatch.StartNew();
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = _repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            AddArguments(startInfo, "list", _solutionPath, "package", "--vulnerable", "--include-transitive");
            var result = await RunProcessAsync(startInfo, cancellationToken).ConfigureAwait(false);
            var findings = new List<string>();
            if (result.ExitCode != 0)
                findings.Add($"Dependency vulnerability scan failed with exit code {result.ExitCode}.");
            else if (result.Output.Contains("has the following vulnerable packages", StringComparison.OrdinalIgnoreCase))
                findings.Add("The dependency scan reported vulnerable packages.");

            foreach (var path in Directory.EnumerateFiles(_backendRoot, "appsettings*.json", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                    && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                foreach (Match match in SecretPattern.Matches(text))
                    findings.Add($"{Path.GetRelativePath(_repositoryRoot, path)} contains a configured value for a secret-like key.");
            }

            var output = result.Output + Environment.NewLine +
                (findings.Count == 0 ? "No dependency vulnerabilities or secret-like appsettings values detected."
                    : string.Join(Environment.NewLine, findings));
            var logPath = Path.Combine(_store.RunDirectory(run.RunId), "logs", "security-scan.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            await File.WriteAllTextAsync(logPath, output, cancellationToken).ConfigureAwait(false);
            return StageOutcome.WithCommandResult(
                GetStage(run, definition.Id), findings.Count == 0 ? 0 : 1, output, logPath,
                started.ElapsedMilliseconds);
        }

        private async Task<StageOutcome> RunPolicyCheckAsync(
            WorkflowRun run, StageDefinition definition, CancellationToken cancellationToken)
        {
            var started = Stopwatch.StartNew();
            WorkflowPolicy.Validate(run.Task);
            var output = "Task scope passed the local guardrail. Execution is restricted to this repository; " +
                "agents may not commit, merge, deploy, publish, or modify secrets.";
            var logPath = Path.Combine(_store.RunDirectory(run.RunId), "logs", $"{definition.Id}.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            await File.WriteAllTextAsync(logPath, output, cancellationToken).ConfigureAwait(false);
            return StageOutcome.WithCommandResult(
                GetStage(run, definition.Id), 0, output, logPath, started.ElapsedMilliseconds);
        }

        private async Task<(int ExitCode, string Output)> RunProcessAsync(
            ProcessStartInfo startInfo, CancellationToken cancellationToken)
        {
            using var timeout = new CancellationTokenSource(ProcessTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            using var process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException($"Could not start '{startInfo.FileName}'.");

                var outputTask = process.StandardOutput.ReadToEndAsync(linked.Token);
                var errorTask = process.StandardError.ReadToEndAsync(linked.Token);
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                var output = (await outputTask.ConfigureAwait(false)) + (await errorTask.ConfigureAwait(false));
                return (process.ExitCode, Tail(output, 200000));
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(true);
                if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    throw new TimeoutException($"'{startInfo.FileName}' exceeded the {ProcessTimeout.TotalMinutes:0}-minute stage timeout.");
                throw;
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                return (127, $"Could not start '{startInfo.FileName}': {exception.Message}");
            }
        }

        private async Task CaptureStageOutputAsync(
            WorkflowRun run, StageRun stage, CancellationToken cancellationToken)
        {
            if (stage.ArtifactPath != null)
                stage.ArtifactFingerprint = await HashArtifactAsync(stage.ArtifactPath, cancellationToken).ConfigureAwait(false);

            var definition = _definitions.Single(item => item.Id == stage.StageId);
            if (definition.Kind == StageKind.Agent)
            {
                stage.ChangedFiles = (await _git.GetChangedFilesAsync(cancellationToken).ConfigureAwait(false)).ToList();
                stage.ChangedFileHashes = await _git.HashFilesAsync(stage.ChangedFiles, cancellationToken).ConfigureAwait(false);
            }

            stage.LogFingerprint = await HashArtifactAsync(stage.LogPath, cancellationToken).ConfigureAwait(false);
            var fingerprintText = stage.ArtifactFingerprint + stage.LogFingerprint + string.Join("|",
                stage.ChangedFileHashes.OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => $"{item.Key}:{item.Value}"));
            stage.OutputFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintText)));
            await SaveAsync(run, cancellationToken).ConfigureAwait(false);
        }

        private async Task<string> HashArtifactAsync(string path, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;
            await using var stream = File.OpenRead(path);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }

        private async Task CaptureRollbackCheckpointAsync(WorkflowRun run, CancellationToken cancellationToken)
        {
            if (!run.RollbackAvailable)
                return;
            run.RollbackFiles = (await _git.GetChangedFilesAsync(cancellationToken).ConfigureAwait(false)).ToList();
            run.RollbackFileHashes = await _git.HashFilesAsync(run.RollbackFiles, cancellationToken).ConfigureAwait(false);
            run.RollbackDiffFingerprint = await _git.GetWorkingDiffFingerprintAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task FinalizeSummaryFingerprintAsync(WorkflowRun run, CancellationToken cancellationToken)
        {
            var stage = GetStage(run, "summary");
            stage.ArtifactFingerprint = await HashArtifactAsync(stage.ArtifactPath, cancellationToken).ConfigureAwait(false);
            stage.LogFingerprint = await HashArtifactAsync(stage.LogPath, cancellationToken).ConfigureAwait(false);
            var fingerprint = stage.ArtifactFingerprint + stage.LogFingerprint;
            stage.OutputFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)));
        }

        private async Task WriteFallbackArtifactAsync(
            WorkflowRun run, StageRun stage, string detail, CancellationToken cancellationToken)
        {
            var text = $"# Safe stop: {stage.StageId}\n\n" +
                "The stage exhausted its bounded retry budget. No later stage was started. " +
                "Review the stage logs, resolve the issue manually, then start a new workflow run.\n\n" +
                $"## Failure\n\n{detail}\n";
            var path = Path.Combine(_store.RunDirectory(run.RunId), "artifacts", $"{stage.StageId}-recovery.md");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, text, cancellationToken).ConfigureAwait(false);
            stage.ArtifactPath = path;
        }

        private async Task WriteSummaryAsync(WorkflowRun run, CancellationToken cancellationToken)
        {
            var total = run.Metrics.SuccessfulStages + run.Metrics.FailedStages;
            run.Metrics.StageSuccessRatePercent = total == 0
                ? 0
                : 100d * run.Metrics.SuccessfulStages / total;
            run.Metrics.RetryFrequencyPercent = run.Metrics.StageAttempts == 0
                ? 0
                : 100d * run.Metrics.RetryCount / run.Metrics.StageAttempts;
            var summary = new StringBuilder();
            summary.AppendLine($"# Engineering Run {run.RunId}");
            summary.AppendLine();
            summary.AppendLine($"- **Task:** {run.Task}");
            summary.AppendLine($"- **Status:** {run.Status}");
            summary.AppendLine($"- **Started:** {run.StartedAtUtc:u}");
            summary.AppendLine($"- **Completed:** {run.CompletedAtUtc:u}");
            summary.AppendLine($"- **End-to-end latency:** {run.Metrics.EndToEndMilliseconds} ms");
            summary.AppendLine($"- **Stage success rate:** {run.Metrics.StageSuccessRatePercent:F1}%");
            summary.AppendLine($"- **Retry frequency:** {run.Metrics.RetryFrequencyPercent:F1}%");
            summary.AppendLine($"- **Retries / fallbacks / rollbacks:** {run.Metrics.RetryCount} / {run.Metrics.FallbackCount} / {run.Metrics.RollbackCount}");
            summary.AppendLine();
            summary.AppendLine("| Stage | Status | Attempts | Duration (ms) | Evidence |");
            summary.AppendLine("|---|---|---:|---:|---|");
            foreach (var stage in run.Stages)
            {
                var evidence = stage.ArtifactPath ?? stage.LogPath ?? stage.LastError ?? string.Empty;
                summary.AppendLine($"| {stage.StageId} | {stage.Status} | {stage.Attempts} | {stage.DurationMilliseconds} | {evidence.Replace("|", "\\|", StringComparison.Ordinal)} |");
            }

            summary.AppendLine();
            summary.AppendLine("## Human approvals");
            foreach (var approval in run.Approvals)
                summary.AppendLine($"- {approval.StageId}: {approval.Approver} at {approval.ApprovedAtUtc:u}");
            if (run.Approvals.Count == 0)
                summary.AppendLine("- None");
            summary.AppendLine();
            summary.AppendLine("## Rollback");
            summary.AppendLine(run.RollbackAvailable
                ? $"Guarded rollback is available from baseline `{run.BaselineCommit}` only if the worktree still exactly matches the recorded output checkpoint. Use `rollback {run.RunId} --confirm`."
                : run.RollbackUnavailableReason);

            await _store.WriteArtifactAsync(run, "engineering-summary.md", summary.ToString(), cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task SaveAsync(WorkflowRun run, CancellationToken cancellationToken)
        {
            run.UpdatedAtUtc = DateTimeOffset.UtcNow;
            var totalStages = run.Metrics.SuccessfulStages + run.Metrics.FailedStages;
            run.Metrics.StageSuccessRatePercent = totalStages == 0
                ? 0
                : 100d * run.Metrics.SuccessfulStages / totalStages;
            run.Metrics.RetryFrequencyPercent = run.Metrics.StageAttempts == 0
                ? 0
                : 100d * run.Metrics.RetryCount / run.Metrics.StageAttempts;
            run.Metrics.EndToEndMilliseconds = (long)(
                (run.CompletedAtUtc ?? run.UpdatedAtUtc) - run.StartedAtUtc).TotalMilliseconds;
            await _store.SaveAsync(run, cancellationToken).ConfigureAwait(false);
        }

        private async Task AuditAsync(
            WorkflowRun run, string eventType, string stageId, string actor, string detail,
            CancellationToken cancellationToken)
        {
            await _store.AppendAuditAsync(new AuditEvent
            {
                RunId = run.RunId,
                StageId = stageId,
                EventType = eventType,
                Actor = actor,
                Detail = detail,
                TimestampUtc = DateTimeOffset.UtcNow
            }, cancellationToken).ConfigureAwait(false);
            await SaveAsync(run, cancellationToken).ConfigureAwait(false);
        }

        private void MarkBlockedStages(WorkflowRun run)
        {
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var stage in run.Stages.Where(stage => stage.Status == StageStatus.Pending))
                {
                    var definition = _definitions.Single(item => item.Id == stage.StageId);
                    if (definition.DependsOn.Any(dependency =>
                        run.Stages.Single(item => item.StageId == dependency).Status is StageStatus.Failed or StageStatus.Blocked))
                    {
                        stage.Status = StageStatus.Blocked;
                        changed = true;
                    }
                }
            }
        }

        private static StageRun GetStage(WorkflowRun run, string id) =>
            run.Stages.Single(stage => stage.StageId == id);

        private static void AddArguments(ProcessStartInfo startInfo, params string[] arguments)
        {
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
        }

        private static string Tail(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[^maxLength..];

        private sealed record StageOutcome(
            StageRun Stage, bool Success, int ExitCode, string Output, string Detail, string LogPath,
            long DurationMilliseconds)
        {
            public static StageOutcome Succeeded(StageRun stage, long duration, string detail) =>
                new(stage, true, 0, string.Empty, detail, null, duration);

            public static StageOutcome Failed(StageRun stage, long duration, string detail) =>
                new(stage, false, 1, string.Empty, detail, null, duration);

            public static StageOutcome WithCommandResult(
                StageRun stage, int exitCode, string output, string logPath, long duration) =>
                new(stage, exitCode == 0, exitCode, output, string.Empty, logPath, duration);
        }
    }
}

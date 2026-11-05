using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CS.AgenticUrlShortner.Orchestrator
{
    internal static class Program
    {
        private static readonly CancellationTokenSource Shutdown = new();

        private static async Task<int> Main(string[] args)
        {
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                Shutdown.Cancel();
            };

            try
            {
                var repositoryRoot = FindRepositoryRoot(Directory.GetCurrentDirectory());
                if (repositoryRoot == null)
                {
                    Console.Error.WriteLine("Run this command from the repository root or one of its subdirectories.");
                    return 1;
                }

                var runner = new WorkflowRunner(repositoryRoot);
                if (args.Length == 0)
                    return Usage();

                switch (args[0].ToLowerInvariant())
                {
                    case "run":
                        return await RunNewAsync(runner, args.Skip(1).ToArray()).ConfigureAwait(false);
                    case "resume":
                        return await ResumeAsync(runner, args).ConfigureAwait(false);
                    case "approve":
                        return await ApproveAsync(runner, args).ConfigureAwait(false);
                    case "replan":
                        return await ReplanAsync(runner, args).ConfigureAwait(false);
                    case "status":
                        return await StatusAsync(runner, args).ConfigureAwait(false);
                    case "rollback":
                        return await RollbackAsync(runner, repositoryRoot, args).ConfigureAwait(false);
                    default:
                        return Usage();
                }
            }
            catch (Exception exception) when (exception is ArgumentException
                or InvalidOperationException or IOException or TimeoutException)
            {
                Console.Error.WriteLine(exception.Message);
                return 1;
            }
        }

        private static async Task<int> RunNewAsync(WorkflowRunner runner, string[] taskParts)
        {
            var task = string.Join(" ", taskParts).Trim();
            using (runner.AcquireWorkspaceLock())
            {
                var run = await runner.CreateRunAsync(task, Shutdown.Token).ConfigureAwait(false);
                Console.WriteLine($"Created run {run.RunId}. Artifacts and audit log: AgentUrlShortner/orchestration/.runtime/runs/{run.RunId}");
                run = await runner.ExecuteAsync(run, Shutdown.Token).ConfigureAwait(false);
                PrintRun(run);
                return ExitCode(run);
            }
        }

        private static async Task<int> ResumeAsync(WorkflowRunner runner, string[] args)
        {
            if (args.Length != 2)
                return Usage();
            using (runner.AcquireWorkspaceLock())
            {
                var run = await runner.LoadRunAsync(args[1], Shutdown.Token).ConfigureAwait(false);
                EnsureResumable(run);
                run = await runner.ExecuteAsync(run, Shutdown.Token).ConfigureAwait(false);
                PrintRun(run);
                return ExitCode(run);
            }
        }

        private static async Task<int> ApproveAsync(WorkflowRunner runner, string[] args)
        {
            if (args.Length != 4)
                return Usage();
            using (runner.AcquireWorkspaceLock())
            {
                var run = await runner.LoadRunAsync(args[1], Shutdown.Token).ConfigureAwait(false);
                await runner.ApproveAsync(run, args[2], args[3], Shutdown.Token).ConfigureAwait(false);
                run = await runner.ExecuteAsync(run, Shutdown.Token).ConfigureAwait(false);
                PrintRun(run);
                return ExitCode(run);
            }
        }

        private static async Task<int> ReplanAsync(WorkflowRunner runner, string[] args)
        {
            if (args.Length != 3)
                return Usage();
            using (runner.AcquireWorkspaceLock())
            {
                var run = await runner.LoadRunAsync(args[1], Shutdown.Token).ConfigureAwait(false);
                await runner.ReplanAsync(run, args[2], Shutdown.Token).ConfigureAwait(false);
                run = await runner.ExecuteAsync(run, Shutdown.Token).ConfigureAwait(false);
                PrintRun(run);
                return ExitCode(run);
            }
        }

        private static async Task<int> StatusAsync(WorkflowRunner runner, string[] args)
        {
            if (args.Length != 2)
                return Usage();
            var run = await runner.LoadRunAsync(args[1], Shutdown.Token).ConfigureAwait(false);
            PrintRun(run);
            return 0;
        }

        private static async Task<int> RollbackAsync(
            WorkflowRunner runner, string repositoryRoot, string[] args)
        {
            if (args.Length != 3 || !string.Equals(args[2], "--confirm", StringComparison.Ordinal))
                return Usage();
            using (runner.AcquireWorkspaceLock())
            {
                var run = await runner.LoadRunAsync(args[1], Shutdown.Token).ConfigureAwait(false);
                if (run.Status is not ("Succeeded" or "Failed" or "Blocked" or "Stopped"))
                    throw new InvalidOperationException("Rollback is only available after a workflow has stopped.");

                await new GitWorkspace(repositoryRoot).TryRollbackAsync(run, Shutdown.Token).ConfigureAwait(false);
                run.Status = "RolledBack";
                run.UpdatedAtUtc = DateTimeOffset.UtcNow;
                run.CompletedAtUtc = DateTimeOffset.UtcNow;
                await runner.RecordRollbackAsync(run, Shutdown.Token).ConfigureAwait(false);
                Console.WriteLine($"Run {run.RunId} was rolled back to {run.BaselineCommit}.");
                return 0;
            }
        }

        private static void EnsureResumable(WorkflowRun run)
        {
            if (run.Status is "Succeeded" or "RolledBack")
                throw new InvalidOperationException($"Run '{run.RunId}' has status '{run.Status}'. Use 'replan' to intentionally rerun a stage.");
            if (run.Status is "Failed" or "Blocked")
                throw new InvalidOperationException($"Run '{run.RunId}' stopped with status '{run.Status}'. Resolve the issue, then use 'replan {run.RunId} <stage>'.");
        }

        private static int ExitCode(WorkflowRun run) => run.Status switch
        {
            "Succeeded" => 0,
            "AwaitingApproval" => 2,
            "Stopped" => 130,
            _ => 1
        };

        private static void PrintRun(WorkflowRun run)
        {
            Console.WriteLine($"Run: {run.RunId} | Status: {run.Status}");
            foreach (var stage in run.Stages)
                Console.WriteLine($"  {stage.StageId,-20} {stage.Status,-18} attempts={stage.Attempts}");
            Console.WriteLine($"Run metrics: attempts={run.Metrics.StageAttempts}; retries={run.Metrics.RetryCount}; fallbacks={run.Metrics.FallbackCount}; rollbacks={run.Metrics.RollbackCount}; duration={run.Metrics.EndToEndMilliseconds} ms");
            if (run.Status == "AwaitingApproval")
            {
                var stage = run.Stages.Single(item => item.Status == StageStatus.AwaitingApproval);
                Console.WriteLine($"Approval required: dotnet run --project \"AgentUrlShortner/Backend/8. Tools/CS.AgenticUrlShortner.Orchestrator/CS.AgenticUrlShortner.Orchestrator.csproj\" -- approve {run.RunId} {stage.StageId} <approver>");
            }
        }

        private static int Usage()
        {
            Console.Error.WriteLine("Usage:");
            Console.Error.WriteLine("  orchestrator run \"<engineering task>\"");
            Console.Error.WriteLine("  orchestrator status <runId>");
            Console.Error.WriteLine("  orchestrator approve <runId> <stageId> <approver>");
            Console.Error.WriteLine("  orchestrator resume <runId>");
            Console.Error.WriteLine("  orchestrator replan <runId> <stageId>");
            Console.Error.WriteLine("  orchestrator rollback <runId> --confirm");
            return 1;
        }

        private static string FindRepositoryRoot(string start)
        {
            var directory = new DirectoryInfo(start);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AgentUrlShortner", "Backend", "CS.AgenticUrlShortner.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }

            return null;
        }
    }
}

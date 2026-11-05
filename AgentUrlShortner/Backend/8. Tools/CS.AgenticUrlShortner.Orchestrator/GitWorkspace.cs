using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CS.AgenticUrlShortner.Orchestrator
{
    internal sealed class GitWorkspace
    {
        private readonly string _repositoryRoot;

        public GitWorkspace(string repositoryRoot)
        {
            _repositoryRoot = repositoryRoot;
        }

        public async Task<(int ExitCode, string Output)> RunGitAsync(
            IEnumerable<string> arguments, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = _repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start git.");
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return (process.ExitCode,
                (await outputTask.ConfigureAwait(false)) + (await errorTask.ConfigureAwait(false)));
        }

        public async Task<string> GetHeadAsync(CancellationToken cancellationToken)
        {
            var result = await RunGitAsync(["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"Could not read the repository commit: {result.Output}");
            return result.Output.Trim();
        }

        public async Task<bool> IsCleanAsync(CancellationToken cancellationToken)
        {
            var result = await RunGitAsync(
                ["status", "--porcelain", "--untracked-files=all"], cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"Could not inspect repository changes: {result.Output}");
            return string.IsNullOrWhiteSpace(result.Output);
        }

        public async Task<IReadOnlyList<string>> GetChangedFilesAsync(CancellationToken cancellationToken)
        {
            var result = await RunGitAsync(
                ["status", "--porcelain", "--untracked-files=all", "--no-renames", "-z"],
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"Could not inspect changed files: {result.Output}");

            var entries = result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            return entries.Select(entry => entry.Length > 3 ? entry[3..] : string.Empty)
                .Where(path => path.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        public async Task<Dictionary<string, string>> HashFilesAsync(
            IEnumerable<string> paths, CancellationToken cancellationToken)
        {
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = Path.GetFullPath(Path.Combine(_repositoryRoot, path));
                if (!fullPath.StartsWith(_repositoryRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Changed path escapes the repository: '{path}'.");
                if (!File.Exists(fullPath))
                {
                    hashes[path] = "missing";
                    continue;
                }

                if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException($"Rollback safety check does not support symbolic links: '{path}'.");
                await using var stream = File.OpenRead(fullPath);
                var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                hashes[path] = Convert.ToHexString(hash);
            }

            return hashes;
        }

        public async Task<string> GetWorkingDiffFingerprintAsync(CancellationToken cancellationToken)
        {
            var result = await RunGitAsync(["diff", "--binary", "HEAD"], cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"Could not inspect the working diff: {result.Output}");
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.Output)));
        }

        public async Task<bool> TryRollbackAsync(WorkflowRun run, CancellationToken cancellationToken)
        {
            if (!run.RollbackAvailable || string.IsNullOrWhiteSpace(run.BaselineCommit))
                throw new InvalidOperationException(run.RollbackUnavailableReason ?? "Rollback is not available for this run.");

            var currentFiles = await GetChangedFilesAsync(cancellationToken).ConfigureAwait(false);
            var expectedFiles = run.RollbackFiles.OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (!currentFiles.OrderBy(path => path, StringComparer.Ordinal).SequenceEqual(expectedFiles, StringComparer.Ordinal))
                throw new InvalidOperationException("Rollback stopped: repository files changed after the workflow checkpoint.");
            var currentDiff = await GetWorkingDiffFingerprintAsync(cancellationToken).ConfigureAwait(false);
            if (currentDiff != run.RollbackDiffFingerprint)
                throw new InvalidOperationException("Rollback stopped: the staged or unstaged diff changed after the workflow checkpoint.");

            var hashes = await HashFilesAsync(expectedFiles, cancellationToken).ConfigureAwait(false);
            if (run.RollbackFileHashes.Any(item =>
                !hashes.TryGetValue(item.Key, out var currentHash) || currentHash != item.Value))
                throw new InvalidOperationException("Rollback stopped: a workflow output differs from its recorded checkpoint.");

            var baselineFilesResult = await RunGitAsync(
                ["ls-tree", "-r", "--name-only", "-z", run.BaselineCommit], cancellationToken).ConfigureAwait(false);
            if (baselineFilesResult.ExitCode != 0)
                throw new InvalidOperationException($"Could not inspect the rollback baseline: {baselineFilesResult.Output}");
            var baselineFiles = new HashSet<string>(
                baselineFilesResult.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries),
                StringComparer.Ordinal);

            foreach (var path in expectedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (baselineFiles.Contains(path))
                {
                    var result = await RunGitAsync(
                        ["restore", "--source", run.BaselineCommit, "--staged", "--worktree", "--", path],
                        cancellationToken).ConfigureAwait(false);
                    if (result.ExitCode != 0)
                        throw new InvalidOperationException($"Could not restore '{path}': {result.Output}");
                }
                else
                {
                    var fullPath = Path.GetFullPath(Path.Combine(_repositoryRoot, path));
                    if (File.Exists(fullPath))
                        File.Delete(fullPath);
                }
            }

            run.Metrics.RollbackCount++;
            return true;
        }
    }
}

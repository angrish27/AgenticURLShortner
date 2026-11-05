using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CS.AgenticUrlShortner.Orchestrator;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CS.AgenticUrlShortner.OrchestratorTests
{
    [TestClass]
    public sealed class GitWorkspaceTests
    {
        [TestMethod]
        public async Task TryRollbackAsync_ExactCheckpoint_RestoresTrackedAndRemovesCreatedFiles()
        {
            var repository = CreateRepository();
            try
            {
                var baseline = InitializeRepository(repository);
                var trackedPath = Path.Combine(repository, "tracked file.txt");
                var newPath = Path.Combine(repository, "generated.txt");
                await File.WriteAllTextAsync(trackedPath, "after").ConfigureAwait(false);
                await File.WriteAllTextAsync(newPath, "generated").ConfigureAwait(false);

                var workspace = new GitWorkspace(repository);
                var run = await CreateRollbackRunAsync(workspace, baseline).ConfigureAwait(false);

                await workspace.TryRollbackAsync(run, CancellationToken.None).ConfigureAwait(false);

                Assert.AreEqual("before", await File.ReadAllTextAsync(trackedPath).ConfigureAwait(false));
                Assert.IsFalse(File.Exists(newPath));
                Assert.IsTrue(await workspace.IsCleanAsync(CancellationToken.None).ConfigureAwait(false));
                Assert.AreEqual(1, run.Metrics.RollbackCount);
            }
            finally
            {
                Directory.Delete(repository, true);
            }
        }

        [TestMethod]
        public async Task TryRollbackAsync_ChangedAfterCheckpoint_RefusesWithoutModifyingFiles()
        {
            var repository = CreateRepository();
            try
            {
                var baseline = InitializeRepository(repository);
                var trackedPath = Path.Combine(repository, "tracked.txt");
                await File.WriteAllTextAsync(trackedPath, "workflow output").ConfigureAwait(false);
                var workspace = new GitWorkspace(repository);
                var run = await CreateRollbackRunAsync(workspace, baseline).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(repository, "later-edit.txt"), "preserve").ConfigureAwait(false);

                await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
                    await workspace.TryRollbackAsync(run, CancellationToken.None).ConfigureAwait(false));

                Assert.AreEqual("workflow output", await File.ReadAllTextAsync(trackedPath).ConfigureAwait(false));
            }
            finally
            {
                Directory.Delete(repository, true);
            }
        }

        private static string CreateRepository()
        {
            var path = Path.Combine(Path.GetTempPath(), $"orchestrator-git-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return path;
        }

        private static string InitializeRepository(string repository)
        {
            RunGit(repository, "init", "-q");
            RunGit(repository, "config", "user.email", "orchestrator-tests@example.invalid");
            RunGit(repository, "config", "user.name", "Orchestrator Tests");
            File.WriteAllText(Path.Combine(repository, "tracked file.txt"), "before");
            RunGit(repository, "add", "--", "tracked file.txt");
            RunGit(repository, "commit", "-m", "baseline", "--quiet");
            return RunGit(repository, "rev-parse", "HEAD").Trim();
        }

        private static async Task<WorkflowRun> CreateRollbackRunAsync(GitWorkspace workspace, string baseline)
        {
            var files = await workspace.GetChangedFilesAsync(CancellationToken.None).ConfigureAwait(false);
            return new WorkflowRun
            {
                BaselineCommit = baseline,
                RollbackAvailable = true,
                RollbackFiles = files.ToList(),
                RollbackFileHashes = await workspace.HashFilesAsync(files, CancellationToken.None).ConfigureAwait(false),
                RollbackDiffFingerprint = await workspace.GetWorkingDiffFingerprintAsync(CancellationToken.None).ConfigureAwait(false)
            };
        }

        private static string RunGit(string repository, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = repository,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start git for the test.");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"git {string.Join(" ", arguments)} failed: {error}");
            return output;
        }
    }
}

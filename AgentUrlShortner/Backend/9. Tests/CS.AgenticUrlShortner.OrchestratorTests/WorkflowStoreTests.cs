using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CS.AgenticUrlShortner.Orchestrator;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CS.AgenticUrlShortner.OrchestratorTests
{
    [TestClass]
    public sealed class WorkflowStoreTests
    {
        [TestMethod]
        public async Task SaveAndLoad_PreservesRunAndStageState()
        {
            var root = CreateWorkspace();
            try
            {
                var store = new WorkflowStore(root);
                var run = new WorkflowRun
                {
                    RunId = "test-run-1",
                    Task = "test task",
                    Status = "Running",
                    Stages = [new StageRun { StageId = "design", Status = StageStatus.AwaitingApproval, Attempts = 1 }]
                };

                await store.SaveAsync(run, CancellationToken.None).ConfigureAwait(false);
                var loaded = await store.LoadAsync(run.RunId, CancellationToken.None).ConfigureAwait(false);

                Assert.AreEqual(run.Task, loaded.Task);
                Assert.AreEqual(StageStatus.AwaitingApproval, loaded.Stages[0].Status);
                Assert.AreEqual(1, loaded.Stages[0].Attempts);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [TestMethod]
        public void RunDirectory_RejectsPathTraversal()
        {
            var root = CreateWorkspace();
            try
            {
                var store = new WorkflowStore(root);
                Assert.ThrowsException<ArgumentException>(() => store.RunDirectory("../outside"));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static string CreateWorkspace()
        {
            var root = Path.Combine(Path.GetTempPath(), $"orchestrator-store-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(root, "AgentUrlShortner", "orchestration"));
            return root;
        }
    }
}

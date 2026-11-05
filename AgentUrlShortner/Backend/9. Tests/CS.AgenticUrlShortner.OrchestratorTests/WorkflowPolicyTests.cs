using System;
using CS.AgenticUrlShortner.Orchestrator;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CS.AgenticUrlShortner.OrchestratorTests
{
    [TestClass]
    public sealed class WorkflowPolicyTests
    {
        [TestMethod]
        public void Validate_AcceptsDefensiveEngineeringTask()
        {
            WorkflowPolicy.Validate("Add validation and tests for URL expiration handling.");
        }

        [TestMethod]
        public void Validate_RejectsOffensiveTask()
        {
            Assert.ThrowsException<InvalidOperationException>(() =>
                WorkflowPolicy.Validate("Create malware to steal credentials."));
        }

        [TestMethod]
        public void Validate_RejectsOversizedTask()
        {
            Assert.ThrowsException<ArgumentException>(() =>
                WorkflowPolicy.Validate(new string('a', 12001)));
        }
    }
}

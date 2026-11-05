using System;
using System.Collections.Generic;
using System.Linq;
using CS.AgenticUrlShortner.Orchestrator;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CS.AgenticUrlShortner.OrchestratorTests
{
    [TestClass]
    public sealed class WorkflowPlanTests
    {
        [TestMethod]
        public void Create_ContainsParallelVerificationAndApprovalGates()
        {
            var definitions = WorkflowPlan.Create();
            var run = CreateRun(definitions);
            foreach (var id in new[] { "policy-gate", "requirements", "design", "design-approval", "implementation" })
                SetSucceeded(run, id, $"{id}-output");
            SetSucceeded(run, "build", "build-output");

            var ready = WorkflowPlan.ReadyStages(definitions, run)
                .Select(stage => stage.StageId)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            CollectionAssert.AreEqual(new[] { "api-tests", "security-scan", "unit-tests" }, ready);
            Assert.IsTrue(definitions.Single(stage => stage.Id == "design-approval").Kind == StageKind.Approval);
            Assert.IsTrue(definitions.Single(stage => stage.Id == "release-approval").Kind == StageKind.Approval);
        }

        [TestMethod]
        public void UpstreamStageIds_ProvidesTransitiveContextToImplementation()
        {
            var ancestors = WorkflowPlan.UpstreamStageIds(WorkflowPlan.Create(), "implementation");

            CollectionAssert.Contains(ancestors.ToArray(), "requirements");
            CollectionAssert.Contains(ancestors.ToArray(), "design");
            CollectionAssert.Contains(ancestors.ToArray(), "design-approval");
        }

        [TestMethod]
        public void ArtifactValidator_RejectsMissingRequiredHeadings()
        {
            var definition = WorkflowPlan.Create().Single(stage => stage.Id == "requirements");

            var missing = WorkflowArtifactValidator.MissingSections(definition,
                "## Goals\n## Acceptance Criteria\n## Assumptions\n");

            CollectionAssert.AreEqual(
                new[] { "Blocking Questions", "Risks", "Out of Scope" }, missing);
        }

        [TestMethod]
        public void ArtifactValidator_AcceptsCompleteHeadingsCaseInsensitively()
        {
            var definition = WorkflowPlan.Create().Single(stage => stage.Id == "requirements");
            var artifact = string.Join(Environment.NewLine,
                definition.RequiredArtifactSections.Select(section => $"## {section.ToLowerInvariant()}"));

            CollectionAssert.AreEqual(Array.Empty<string>(),
                WorkflowArtifactValidator.MissingSections(definition, artifact));
        }

        [TestMethod]
        public void Validate_RejectsCycles()
        {
            var definitions = new[]
            {
                new StageDefinition { Id = "a", DependsOn = ["b"] },
                new StageDefinition { Id = "b", DependsOn = ["a"] }
            };

            Assert.ThrowsException<InvalidOperationException>(() => WorkflowPlan.Validate(definitions));
        }

        [TestMethod]
        public void Validate_RejectsUnknownDependency()
        {
            var definitions = new[]
            {
                new StageDefinition { Id = "a", DependsOn = ["missing"] }
            };

            Assert.ThrowsException<InvalidOperationException>(() => WorkflowPlan.Validate(definitions));
        }

        [TestMethod]
        public void Validate_RejectsMissingStageId()
        {
            var definitions = new[]
            {
                new StageDefinition { Id = " " }
            };

            Assert.ThrowsException<InvalidOperationException>(() => WorkflowPlan.Validate(definitions));
        }

        [TestMethod]
        public void InputFingerprint_ChangesWhenUpstreamOutputChanges()
        {
            var definitions = WorkflowPlan.Create();
            var run = CreateRun(definitions);
            SetSucceeded(run, "requirements", "requirements-v1");
            var stage = definitions.Single(item => item.Id == "design");
            var original = WorkflowPlan.InputFingerprint(stage, run);

            SetSucceeded(run, "requirements", "requirements-v2");

            Assert.AreNotEqual(original, WorkflowPlan.InputFingerprint(stage, run));
        }

        [TestMethod]
        public void InvalidateStaleStages_ResetsAllDownstreamWork()
        {
            var definitions = WorkflowPlan.Create();
            var run = CreateRun(definitions);
            foreach (var stage in run.Stages)
            {
                var definition = definitions.Single(item => item.Id == stage.StageId);
                stage.Status = StageStatus.Succeeded;
                stage.OutputFingerprint = $"output-{stage.StageId}";
                stage.ApprovedBy = definition.Kind == StageKind.Approval ? "reviewer" : null;
            }
            foreach (var definition in definitions)
                run.Stages.Single(stage => stage.StageId == definition.Id).InputFingerprint =
                    WorkflowPlan.InputFingerprint(definition, run);

            run.Stages.Single(stage => stage.StageId == "requirements").Status = StageStatus.Pending;
            WorkflowPlan.InvalidateStaleStages(definitions, run);

            Assert.AreEqual(StageStatus.Pending, run.Stages.Single(stage => stage.StageId == "requirements").Status);
            Assert.AreEqual(StageStatus.Pending, run.Stages.Single(stage => stage.StageId == "design").Status);
            Assert.AreEqual(StageStatus.Pending, run.Stages.Single(stage => stage.StageId == "implementation").Status);
            Assert.AreEqual(StageStatus.Pending, run.Stages.Single(stage => stage.StageId == "api-tests").Status);
            Assert.IsNull(run.Stages.Single(stage => stage.StageId == "design-approval").ApprovedBy);
        }

        [TestMethod]
        public void InvalidateStaleStages_RevokesApprovalForChangedDesign()
        {
            var definitions = WorkflowPlan.Create();
            var run = CreateRun(definitions);
            foreach (var stage in run.Stages)
            {
                stage.Status = StageStatus.Succeeded;
                stage.OutputFingerprint = $"output-{stage.StageId}";
            }
            foreach (var definition in definitions)
                run.Stages.Single(stage => stage.StageId == definition.Id).InputFingerprint =
                    WorkflowPlan.InputFingerprint(definition, run);

            run.Stages.Single(stage => stage.StageId == "design").Status = StageStatus.Pending;
            var approval = run.Stages.Single(stage => stage.StageId == "design-approval");
            approval.Status = StageStatus.Pending;
            approval.ApprovedBy = "reviewer";

            WorkflowPlan.InvalidateStaleStages(definitions, run);

            Assert.IsNull(approval.ApprovedBy);
        }

        private static WorkflowRun CreateRun(IReadOnlyCollection<StageDefinition> definitions) =>
            new()
            {
                Task = "test task",
                Stages = definitions.Select(definition => new StageRun
                {
                    StageId = definition.Id,
                    Status = StageStatus.Pending
                }).ToList()
            };

        private static void SetSucceeded(WorkflowRun run, string id, string output)
        {
            var stage = run.Stages.Single(item => item.StageId == id);
            stage.Status = StageStatus.Succeeded;
            stage.OutputFingerprint = output;
        }
    }
}

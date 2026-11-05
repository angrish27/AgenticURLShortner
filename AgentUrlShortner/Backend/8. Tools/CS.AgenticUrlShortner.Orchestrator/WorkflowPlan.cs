using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CS.AgenticUrlShortner.Orchestrator
{
    internal static class WorkflowPlan
    {
        public static IReadOnlyList<StageDefinition> Create()
        {
            StageDefinition[] stages =
            [
                new StageDefinition
                {
                    Id = "policy-gate",
                    Name = "Task safety and change-control policy",
                    Kind = StageKind.Policy
                },
                new StageDefinition
                {
                    Id = "requirements",
                    Name = "Requirement analysis",
                    Kind = StageKind.Agent,
                    DependsOn = ["policy-gate"],
                    Instructions = "Normalize the task into goals, acceptance criteria, assumptions, ambiguities, risks, and out-of-scope items. Do not edit production code.",
                    ArtifactName = "requirements.md",
                    RequiredArtifactSections = ["Goals", "Acceptance Criteria", "Assumptions", "Blocking Questions", "Risks", "Out of Scope"],
                    MaxAttempts = 2
                },
                new StageDefinition
                {
                    Id = "design",
                    Name = "Architecture and execution plan",
                    Kind = StageKind.Agent,
                    DependsOn = ["requirements"],
                    Instructions = "Propose a minimal design, impacted modules and data flows, alternatives, trade-offs, test plan, rollout and rollback considerations. Do not edit production code.",
                    ArtifactName = "design.md",
                    RequiredArtifactSections = ["Architecture", "Impacted Areas", "Task Graph", "Alternatives", "Trade-offs", "Validation", "Rollback"],
                    MaxAttempts = 2
                },
                new StageDefinition
                {
                    Id = "design-approval",
                    Name = "Human design approval",
                    Kind = StageKind.Approval,
                    DependsOn = ["design"]
                },
                new StageDefinition
                {
                    Id = "implementation",
                    Name = "Implementation and tests",
                    Kind = StageKind.Agent,
                    DependsOn = ["design-approval"],
                    Instructions = "Implement only the approved task/design. Add or update tests, follow repository instructions, preserve existing user changes, and do not commit, merge, or deploy.",
                    ArtifactName = "implementation.md",
                    RequiredArtifactSections = ["Changes", "Tests", "Risks"],
                    MaxAttempts = 2
                },
                new StageDefinition
                {
                    Id = "build",
                    Name = "Release build",
                    Kind = StageKind.Build,
                    DependsOn = ["implementation"]
                },
                new StageDefinition
                {
                    Id = "unit-tests",
                    Name = "Unit and layer tests",
                    Kind = StageKind.UnitTests,
                    DependsOn = ["build"]
                },
                new StageDefinition
                {
                    Id = "api-tests",
                    Name = "API integration tests",
                    Kind = StageKind.ApiTests,
                    DependsOn = ["build"]
                },
                new StageDefinition
                {
                    Id = "security-scan",
                    Name = "Dependency and secret checks",
                    Kind = StageKind.Security,
                    DependsOn = ["build"]
                },
                new StageDefinition
                {
                    Id = "documentation",
                    Name = "Documentation and engineering summary",
                    Kind = StageKind.Agent,
                    DependsOn = ["requirements", "design", "unit-tests", "api-tests", "security-scan"],
                    Instructions = "Update only directly related documentation. Ensure greenfield, brownfield, and ambiguous scenarios, setup, testing, limitations, and decisions are accurate. Produce an engineering summary with artifacts, validation evidence, assumptions, risks, and trade-offs.",
                    ArtifactName = "documentation.md",
                    RequiredArtifactSections = ["Artifacts", "Validation", "Assumptions", "Risks", "Limitations"],
                    MaxAttempts = 2
                },
                new StageDefinition
                {
                    Id = "release-approval",
                    Name = "Human release approval",
                    Kind = StageKind.Approval,
                    DependsOn = ["documentation"]
                },
                new StageDefinition
                {
                    Id = "release-readiness",
                    Name = "Release readiness",
                    Kind = StageKind.ReleaseReadiness,
                    DependsOn = ["release-approval", "unit-tests", "api-tests", "security-scan"]
                },
                new StageDefinition
                {
                    Id = "summary",
                    Name = "Run summary and metrics",
                    Kind = StageKind.Summary,
                    DependsOn = ["release-readiness"]
                }
            ];

            Validate(stages);
            return stages;
        }

        public static void Validate(IReadOnlyCollection<StageDefinition> stages)
        {
            var byId = stages.ToDictionary(stage => stage.Id, StringComparer.Ordinal);
            foreach (var stage in stages)
            {
                if (string.IsNullOrWhiteSpace(stage.Id) || stage.MaxAttempts < 1)
                    throw new InvalidOperationException("Every stage needs an id and at least one attempt.");

                foreach (var dependency in stage.DependsOn)
                {
                    if (!byId.ContainsKey(dependency))
                        throw new InvalidOperationException($"Stage '{stage.Id}' has unknown dependency '{dependency}'.");
                }
            }

            var visited = new HashSet<string>(StringComparer.Ordinal);
            var active = new HashSet<string>(StringComparer.Ordinal);
            foreach (var stage in stages)
                Visit(stage.Id);

            void Visit(string id)
            {
                if (active.Contains(id))
                    throw new InvalidOperationException($"The workflow contains a dependency cycle at '{id}'.");
                if (!visited.Add(id))
                    return;

                active.Add(id);
                foreach (var dependency in byId[id].DependsOn)
                    Visit(dependency);
                active.Remove(id);
            }
        }

        public static string Fingerprint(IReadOnlyCollection<StageDefinition> stages)
        {
            var plan = string.Join("\n", stages.Select(stage =>
                $"{stage.Id}|{stage.Kind}|{stage.MaxAttempts}|{string.Join(",", stage.DependsOn)}|" +
                $"{string.Join(",", stage.RequiredArtifactSections)}|{stage.Instructions}"));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan)));
        }

        public static IReadOnlyList<string> UpstreamStageIds(
            IReadOnlyCollection<StageDefinition> stages, string stageId)
        {
            var byId = stages.ToDictionary(stage => stage.Id, StringComparer.Ordinal);
            if (!byId.ContainsKey(stageId))
                throw new ArgumentException($"Unknown stage '{stageId}'.", nameof(stageId));

            var ancestors = new HashSet<string>(StringComparer.Ordinal);
            void Visit(string id)
            {
                foreach (var dependency in byId[id].DependsOn)
                {
                    if (ancestors.Add(dependency))
                        Visit(dependency);
                }
            }

            Visit(stageId);
            return stages.Where(stage => ancestors.Contains(stage.Id)).Select(stage => stage.Id).ToArray();
        }

        public static string InputFingerprint(StageDefinition stage, WorkflowRun run)
        {
            var inputs = new StringBuilder(run.PlanFingerprint).Append('\n').Append(run.Task);
            foreach (var dependency in stage.DependsOn.OrderBy(id => id, StringComparer.Ordinal))
            {
                var dependencyRun = run.Stages.Single(item => item.StageId == dependency);
                inputs.Append('\n').Append(dependency).Append(':').Append(dependencyRun.OutputFingerprint);
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inputs.ToString())));
        }

        public static IReadOnlyList<StageRun> ReadyStages(
            IReadOnlyList<StageDefinition> definitions, WorkflowRun run)
        {
            return definitions
                .Where(definition => run.Stages.Single(stage => stage.StageId == definition.Id).Status == StageStatus.Pending)
                .Where(definition => definition.DependsOn.All(dependency =>
                    run.Stages.Single(stage => stage.StageId == dependency).Status == StageStatus.Succeeded))
                .Select(definition => run.Stages.Single(stage => stage.StageId == definition.Id))
                .ToArray();
        }

        public static void InvalidateStaleStages(
            IReadOnlyList<StageDefinition> definitions, WorkflowRun run)
        {
            foreach (var definition in definitions)
            {
                var stage = run.Stages.Single(item => item.StageId == definition.Id);
                var dependencyPending = definition.DependsOn.Any(dependency =>
                    run.Stages.Single(item => item.StageId == dependency).Status != StageStatus.Succeeded);
                var inputChanged = stage.Status == StageStatus.Succeeded
                    && stage.InputFingerprint != InputFingerprint(definition, run);
                if (definition.Kind == StageKind.Approval && dependencyPending)
                    stage.ApprovedBy = null;

                if (stage.Status == StageStatus.Succeeded && (dependencyPending || inputChanged))
                {
                    stage.Status = StageStatus.Pending;
                    stage.FinishedAtUtc = null;
                    stage.OutputFingerprint = null;
                    stage.ApprovedBy = null;
                    stage.LastError = "Invalidated because an upstream stage output changed.";
                }
            }
        }
    }
}

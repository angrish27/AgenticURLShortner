using System;
using System.Collections.Generic;

namespace CS.AgenticUrlShortner.Orchestrator
{
    internal enum StageKind
    {
        Policy,
        Agent,
        Approval,
        Build,
        UnitTests,
        ApiTests,
        Security,
        ReleaseReadiness,
        Summary
    }

    internal enum StageStatus
    {
        Pending,
        Running,
        Succeeded,
        Failed,
        AwaitingApproval,
        Blocked
    }

    internal sealed class StageDefinition
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Instructions { get; set; }
        public StageKind Kind { get; set; }
        public string[] DependsOn { get; set; } = Array.Empty<string>();
        public string[] RequiredArtifactSections { get; set; } = Array.Empty<string>();
        public int MaxAttempts { get; set; } = 1;
        public string ArtifactName { get; set; }
    }

    internal sealed class WorkflowRun
    {
        public string RunId { get; set; }
        public string Task { get; set; }
        public string PlanFingerprint { get; set; }
        public string Status { get; set; }
        public DateTimeOffset StartedAtUtc { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public string BaselineCommit { get; set; }
        public bool RollbackAvailable { get; set; }
        public string RollbackUnavailableReason { get; set; }
        public List<string> RollbackFiles { get; set; } = new();
        public Dictionary<string, string> RollbackFileHashes { get; set; } = new(StringComparer.Ordinal);
        public string RollbackDiffFingerprint { get; set; }
        public List<StageRun> Stages { get; set; } = new();
        public List<ApprovalRecord> Approvals { get; set; } = new();
        public WorkflowMetrics Metrics { get; set; } = new();
    }

    internal sealed class StageRun
    {
        public string StageId { get; set; }
        public StageStatus Status { get; set; }
        public int Attempts { get; set; }
        public bool UsedFallback { get; set; }
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset? FinishedAtUtc { get; set; }
        public long DurationMilliseconds { get; set; }
        public string InputFingerprint { get; set; }
        public string OutputFingerprint { get; set; }
        public string ArtifactFingerprint { get; set; }
        public string LogFingerprint { get; set; }
        public string LastError { get; set; }
        public string LogPath { get; set; }
        public string ArtifactPath { get; set; }
        public string ApprovedBy { get; set; }
        public List<string> ChangedFiles { get; set; } = new();
        public Dictionary<string, string> ChangedFileHashes { get; set; } = new(StringComparer.Ordinal);
    }

    internal sealed class ApprovalRecord
    {
        public string StageId { get; set; }
        public string Approver { get; set; }
        public DateTimeOffset ApprovedAtUtc { get; set; }
    }

    internal sealed class WorkflowMetrics
    {
        public int StageAttempts { get; set; }
        public int SuccessfulStages { get; set; }
        public int FailedStages { get; set; }
        public int RetryCount { get; set; }
        public int FallbackCount { get; set; }
        public int RollbackCount { get; set; }
        public double StageSuccessRatePercent { get; set; }
        public double RetryFrequencyPercent { get; set; }
        public long EndToEndMilliseconds { get; set; }
    }

    internal sealed class AuditEvent
    {
        public string EventId { get; set; } = Guid.NewGuid().ToString("N");
        public string RunId { get; set; }
        public string StageId { get; set; }
        public string EventType { get; set; }
        public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
        public string Actor { get; set; }
        public string Detail { get; set; }
    }
}

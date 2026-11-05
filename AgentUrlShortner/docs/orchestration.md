# Agentic SDLC Orchestration

## Architecture

`8. Tools/CS.AgenticUrlShortner.Orchestrator` is a local .NET workflow runner. It keeps orchestration
separate from the URL API and uses no additional service or database. Copilot CLI performs bounded
agent stages; deterministic .NET/Git commands perform build, tests, security checks, and release
readiness.

```text
policy gate -> requirements -> design -> design approval -> implementation -> release build
                                                       ├-> unit tests ──┐
                                                       ├-> API tests ───┼-> documentation
                                                       └-> security ────┘
                      documentation -> release approval -> release readiness -> summary
```

The scheduler validates stage IDs and dependencies, checks required headings in agent artifacts,
applies a local task-safety/change-control gate,
then selects stages whose prerequisites succeeded. It runs at most three ready validation stages
together. A failure prevents downstream work from
starting. Each agent stage can make at most two attempts; exhaustion writes a recovery handoff and
safe-stops the run. There is no automatic commit, merge, or deployment.

## State and lineage

Each run receives a unique ID and persists its task, plan/dependency fingerprints, stage status,
attempts, timestamps, artifacts, output hashes, approvals, and metrics under
`orchestration/.runtime/runs/<runId>/`. Each state update is written atomically. Stage events are
appended to that run's `audit.jsonl`; logs and agent-generated requirements, design, implementation,
documentation, release, and summary artifacts are stored alongside it. The entire runtime directory
is ignored by Git because task text and agent output are local execution data.

On resume, the runner verifies completed artifact/log hashes and changed-file hashes. If an upstream
output no longer matches its checkpoint, that stage and dependent completed stages are invalidated
and re-executed in dependency order. Use `replan <runId> <stageId>` to explicitly reset a stage and
its descendants after a human has resolved a failure or changed scope.

## Governance and recovery

- `design-approval` must be explicitly granted before implementation starts.
- `release-approval` must be explicitly granted before release readiness runs.
- Approval events record the approver and timestamp; they are not inferred from passing tests.
- Ctrl+C terminates the current child process, stops scheduling, and preserves state and logs.
- Child processes have a 20-minute timeout. A run-level workspace lock prevents concurrent runs
  from writing the same worktree.
- Agent retries are bounded at two attempts. Exhaustion produces a recovery artifact and stops;
  it does not invent a successful result.
- Rollback requires `rollback <runId> --confirm`. It is enabled only if the worktree was clean
  before the run, and proceeds only when the current changed paths and file hashes exactly match the
  saved checkpoint. Tracked files are restored to the baseline commit and run-created untracked files
  are removed. A mismatch fails closed without applying the rollback.
- Human review is required before merge. The workflow never merges or deploys.

## Verification gates

The build is a Release configuration with warnings as errors. Unit/layer tests, API integration
tests, and dependency/secret checks are separate parallel stages after the build. Documentation runs
only after these checks pass. Release readiness re-runs the existing release gates and creates an
evidence report. Security checks cover vulnerable NuGet packages and secret-like values in
`appsettings*.json`; they are not a substitute for a full code security review or runtime threat
model.

## Reliability metrics

The run summary records stage success rate, stage attempts, retry frequency, safe-stop fallbacks,
rollback count, and end-to-end latency. These per-run metrics support workflow inspection; they are
not service-level guarantees or statistically meaningful cross-run trends.

## Commands

Run from the repository root; Copilot CLI must be installed and authenticated:

```bash
dotnet run --project "AgentUrlShortner/Backend/8. Tools/CS.AgenticUrlShortner.Orchestrator/CS.AgenticUrlShortner.Orchestrator.csproj" -- run "Describe the task"
dotnet run --project "AgentUrlShortner/Backend/8. Tools/CS.AgenticUrlShortner.Orchestrator/CS.AgenticUrlShortner.Orchestrator.csproj" -- status <runId>
dotnet run --project "AgentUrlShortner/Backend/8. Tools/CS.AgenticUrlShortner.Orchestrator/CS.AgenticUrlShortner.Orchestrator.csproj" -- approve <runId> design-approval "<reviewer>"
dotnet run --project "AgentUrlShortner/Backend/8. Tools/CS.AgenticUrlShortner.Orchestrator/CS.AgenticUrlShortner.Orchestrator.csproj" -- approve <runId> release-approval "<reviewer>"
```

`resume`, `replan`, and guarded `rollback` usage is documented in the repository README.

## Limitations and decisions

- The current process lock and JSON state are local-machine coordination; this is not a distributed
  worker service or multi-user approval system.
- The task policy blocks a limited set of explicitly offensive request indicators. It is a guardrail,
  not a substitute for human review or a complete content-safety classifier.
- Approval identity is user-supplied CLI text and is not authenticated. Treat the local audit log as
  traceability, not tamper-proof compliance evidence.
- Copilot CLI availability, model behavior, credentials, and output quality remain external risks.
- Changes are made in the current Git worktree. A non-clean starting worktree disables rollback;
  use a dedicated clean checkout for stronger isolation.
- Replanning is fingerprint-driven at stage boundaries. It does not infer semantic changes or prove
  that an implementation satisfies the task; automated tests and human review remain necessary.
- Success-rate and retry-frequency measurements are per-run indicators, not production SLOs.

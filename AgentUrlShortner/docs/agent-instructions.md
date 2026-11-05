# Agent Instructions

## Before you start
1. Read [00-master-context.md](00-master-context.md), then the docs relevant to your task.
2. Check [scope.md](scope.md) – do not implement out-of-scope features without explicit approval.
3. Use the URL shortener slice (`ProcessUrlController` / `UrlManager`) as the reference implementation for any new feature.

## Coding rules
- C# targeting `net10.0`, `LangVersion latest`. **Nullable disabled, implicit usings disabled** → add explicit `using`s.
- **Warnings are errors** – the build must be warning-free.
- Block-scoped namespaces matching folder/project (`namespace CS.AgenticUrlShortner.Managers { ... }`), as in existing code.
- Implementations are `internal sealed`, interfaces are `public` in the layer's `Contracts` folder and inherit `IService`.
- Services need a **parameterless constructor**; no constructor injection. Get dependencies through the inherited factories.
- Register every new service in its layer's `Factory.RegisterTypes`.
- Async methods: suffix `Async`, accept `CancellationToken cancellationToken = default`, use `.ConfigureAwait(false)`.
- Throw `BadRequestException` for validation, `AgenticUrlShortnerException` for domain errors; never return error codes from services.
- Controllers inherit `BaseController`, stay thin (one Manager call), and declare `[ProducesResponseType]`.
- Respect layer call rules in [architecture.md](architecture.md). No upward or sideways calls.
- Do not expose `Contracts.Logic` types from controllers.
- No comments unless they explain something non-obvious. Follow `.editorconfig`.
- Do not add NuGet packages unless necessary; prefer what already exists.

## Workflow for a new feature
Use this plan → implement → test → report workflow for code changes:
1. **Plan:** inspect the affected code and docs, identify the intended change, architecture boundaries, and validation.
2. **Implement:** make the change using the existing patterns. For a new feature, add/adjust contracts (Client + Logic) → update [data-models.md](data-models.md); add Utility/Accessor/Engine/Manager bottom-up, each with interface + registration; add the controller endpoint → update [flow-mapping.md](flow-mapping.md).
3. **Test:** add tests per layer following [testing.md](testing.md), update [infra.md](infra.md) for infrastructure changes, and run `dotnet build` and `dotnet test` on `Backend/CS.AgenticUrlShortner.sln`.
4. **Report:** summarize changed behavior, files/docs updated, validation commands and results, and any unresolved issues.
5. **Human review:** a human must review and approve the changes before merge. Agent test results are not approval.

See [agentic-development-example.md](agentic-development-example.md) for an example change report.

## Agentic SDLC orchestrator
The orchestrator in `Backend/8. Tools/CS.AgenticUrlShortner.Orchestrator` executes a persisted,
dependency-gated SDLC plan. Requirement and design artifacts precede an explicit architecture
approval; implementation is followed by a build and parallel unit, API, and security checks.
Documentation and release readiness require those checks to pass. Use the `status`, `approve`,
`resume`, `replan`, and guarded `rollback` commands described in [orchestration.md](orchestration.md).
The agent must preserve unrelated work and must not commit, merge, or deploy.

## Solution structure
- Projects live on disk under the numbered folders (`Backend/3. Managers/...`) and in matching solution folders.
- New projects: place in the right numbered folder and add with
  `dotnet sln Backend/CS.AgenticUrlShortner.sln add <path> --solution-folder "<n>. <Folder>"`.

## Keeping context fresh
Any change to architecture, bootstrap, models, flows, tests, infra or scope MUST update the matching
doc in `docs/` in the same change. Docs that disagree with code are bugs.

## Don'ts
- Don't `new` a service or factory inside services (factories only create downstream factories).
- Don't access `HttpContext` below the Client layer; use `Context` (`AmbientContext`).
- Don't commit secrets, connection strings, or personal data.
- Don't change `Directory.Build.props` rules to silence warnings – fix the cause.

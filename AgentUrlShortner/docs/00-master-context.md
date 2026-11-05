# Master Context – CS.AgenticUrlShortner

> **Agents: read this file first.** It is the entry point for all other context documents.
> Keep every document in `docs/` in sync with the code. If you change architecture, contracts, flows,
> tests, or infrastructure, update the matching document in the same change.

## What this is
A URL shortener service built on **.NET 10** using an **IDesign-style layered architecture**
(Clients → Managers → Engines → Accessors, with Utilities and Common as cross-cutting layers).
The URL shortener feature (`ProcessUrlController` -> `UrlManager` -> `UrlValidationEngine` / `UrlMappingAccessor` / `ShortCodeUtility`)
is the reference vertical slice for all new features.

## Repository layout
```
AgentUrlShortner/
├── Backend/                         # .NET solution (CS.AgenticUrlShortner.sln)
│   ├── 1. Clients/                  # WebApiClient (ASP.NET Core host)
│   ├── 2. Contracts/                # Contracts.Client (DTOs), Contracts.Logic (internal models)
│   ├── 3. Managers/                 # Use-case orchestration
│   ├── 4. Engines/                  # Business rules / validation / algorithms
│   ├── 5. Accessors/                # Data / external resource access
│   ├── 6. Utilities/                # Stateless helpers (ids, time, etc.)
│   ├── 7. Common/                   # Base classes, AmbientContext, exceptions, constants
│   ├── 9. Tests/                    # MSTest projects per layer + Shared.Tests
│   ├── Directory.Build.props        # net10.0, warnings-as-errors, nullable disabled
│   ├── .editorconfig
│   └── CS.AgenticUrlShortner.runsettings
├── Infrastructure/                  # Pulumi (C#) Azure IaC
└── docs/                            # Agent context documents (this folder)
```

## Document index
| Doc | Purpose |
|-----|---------|
| [../../AGENTS.md](../../AGENTS.md) | Repository-wide agent workflow and human review requirement |
| [agent-instructions.md](agent-instructions.md) | Rules agents MUST follow when changing code |
| [agentic-development-example.md](agentic-development-example.md) | Example agent-driven change report |
| [scope.md](scope.md) | What is in / out of scope, feature roadmap |
| [architecture.md](architecture.md) | Layers, dependency rules, project references |
| [bootstrap-chain.md](bootstrap-chain.md) | Startup, DI registration, factory creation chain |
| [data-models.md](data-models.md) | Client and logic contracts, exceptions |
| [flow-mapping.md](flow-mapping.md) | Endpoint → Manager → Engine/Accessor call maps |
| [testing.md](testing.md) | Test projects, base classes, conventions |
| [infra.md](infra.md) | Pulumi stack, Azure resources, config, deployment |
| [orchestration.md](orchestration.md) | Stateful SDLC workflow, approvals, audit, recovery, metrics |

## Key facts (quick reference)
- Target framework: `net10.0`; `Nullable=disable`; `ImplicitUsings=disable`; `TreatWarningsAsErrors=true`.
- All services implement `IService`, are `internal sealed`, have a parameterless constructor and are created **only through factories**.
- Per-request `AmbientContext` (CorrelationId, UserId, Claims) flows through every factory/service.
- Correlation header: `x-correlation-id`. Health endpoint: `/health`.
- Test framework: **MSTest**. Shared test base: `CS.AgenticUrlShortner.Shared.Tests.TestBase`.
- IaC: **Pulumi (azure-native, C#)** → Resource Group, Log Analytics, App Insights, Linux App Service Plan, Web App.
- Agentic workflow: persisted stage DAG in `Backend/8. Tools/CS.AgenticUrlShortner.Orchestrator`; execution state is local and ignored by Git.

## Glossary
| Term | Meaning |
|------|---------|
| Manager | Orchestrates a use case; the only layer Clients may call |
| Engine | Pure business logic/validation; no I/O of its own |
| Accessor | Encapsulates data stores and external resources |
| Utility | Cross-cutting stateless helper callable from any layer |
| AmbientContext | Request-scoped caller/correlation data |

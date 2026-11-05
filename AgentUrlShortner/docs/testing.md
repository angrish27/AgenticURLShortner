# Testing

## Framework
- **MSTest** (`MSTest.TestFramework` 3.x) on `net10.0`.
- Settings: `Backend/CS.AgenticUrlShortner.runsettings`.
- Run: `dotnet test Backend/CS.AgenticUrlShortner.sln --settings Backend/CS.AgenticUrlShortner.runsettings`

## Projects (`Backend/9. Tests`)
| Project | Tests for | Notes |
|---------|-----------|-------|
| `CS.AgenticUrlShortner.Shared.Tests` | – | `TestBase`, shared fakes/builders (not a test project) |
| `Tests.ManagerTests` | Managers | e.g. `UrlManagerTests` |
| `Tests.EngineTests` | Engines | |
| `Tests.AccessorTests` | Accessors | e.g. `UrlMappingStoreTests` |
| `Tests.UtilityTests` | Utilities | |
| `Tests.CommonTests` | Common | e.g. `FactoryBaseTests` |
| `Tests.ApiTests` | HTTP layer (end-to-end) | `WebApplicationFactory<Startup>`, temp store per test; status codes, Location, startup load/corrupt store |
| `CS.AgenticUrlShortner.OrchestratorTests` | Agentic workflow | DAG validation, parallel readiness, fingerprints, policy guardrails, and guarded Git rollback |

Each layer exposes internals to its test project via `[assembly: InternalsVisibleTo("CS.AgenticUrlShortner.Tests.<Layer>Tests")]`.

## TestBase
```csharp
public abstract class TestBase
{
	protected AmbientContext Context { get; }        // CorrelationId = new guid, UserId = "unit-test"
	protected IServiceProvider ServiceProvider { get; } // ManagerFactory.RegisterTypes(services)
	protected ManagerFactory ManagerFactory { get; }
	protected EngineFactory EngineFactory { get; }
	protected AccessorFactory AccessorFactory { get; }
	protected UtilityFactory UtilityFactory { get; }
}
```

## Conventions
- Test class: `<ClassUnderTest>Tests`, inherits `TestBase`, `[TestClass]`.
- Test method: `<Class>_<Scenario>_<Expected>` (e.g. `UrlManager_Encode_InvalidUrl_ThrowsBadRequest`).
- Always obtain the SUT through its factory (`ManagerFactory.Create<IUrlManager>()`), never `new`.
- Include a `TestMe` smoke test per service.
- Isolate a layer by passing fake downstream factories/services into `Create<T>(...)` overloads.
- Exceptions: `Assert.ThrowsExceptionAsync<BadRequestException>(...)`.
- Every new Manager/Engine/Accessor/Utility requires tests for happy path + each validation/error branch.
- Tests must be deterministic: no real network, clock via utility when time matters.

## Definition of done
`dotnet build` (warnings are errors) and `dotnet test` both pass.

Orchestrator rollback tests create isolated temporary Git repositories and verify both successful
rollback and refusal when post-checkpoint changes are present.

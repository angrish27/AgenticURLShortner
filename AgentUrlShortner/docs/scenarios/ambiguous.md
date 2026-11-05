# Ambiguous Scenario - URL Expiration Requirement

## Request

> "Make short links temporary so old links do not keep working forever."

The request does not define the lifetime, whether an expiry can be configured per link, how expiry is
represented by the API, or whether expired mappings should be deleted. Deleting records could also
make previously issued links available for accidental reuse.

## Requirement analysis

Before implementation, the agent should record these unknowns and ask for product decisions:

1. What default lifetime and maximum lifetime should apply?
2. Is the lifetime global, per request, or configurable by user?
3. Should an expired link return `410 Gone`, `404 Not Found`, or another response?
4. Should the mapping remain stored for audit and code non-reuse?
5. How should expiration behave across restart and deployment?

No values are guessed. Until answers are approved, the safe recommendation is to defer expiration
because it changes the public redirect contract and persisted data model.

## Decomposition and dependencies

```text
clarify policy -> approve API and data-model decision -> design migration
      -> expiry contract/validation -> persistence and redirect behavior
      -> unit tests -> API tests -> documentation -> release readiness
```

Expiry policy and storage migration are blocking dependencies; independent API-contract and test-plan
reviews can run in parallel after the policy is approved. A missing policy is a safe-stop condition,
not permission for the agent to choose a default.

## Validation and risk controls

- Test that a live link redirects and an expired link returns the approved status.
- Use an injected clock in unit tests; do not make tests depend on wall-clock delays.
- Verify persisted expiry survives restart and legacy mappings follow the explicitly approved
  compatibility rule.
- Check boundary timestamps, timezone normalization, and clock skew.
- Require human design approval before changing the external contract or storage format.
- Run all unit/API tests, dependency checks, and release-readiness gates before a release approval.

## Decision record

**Current decision:** not implemented. Expiration is deferred in [scope.md](../scope.md) until
product policy and compatibility behavior are approved. No code change should be made from the
ambiguous request alone.

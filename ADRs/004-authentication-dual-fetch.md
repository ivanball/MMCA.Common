# ADR-004: Authentication Dual-Fetch Pattern

## Status
Accepted

## Context
The `AuthenticationService.LoginAsync` flow needs to:
1. Validate the user exists and credentials are correct (read-only check)
2. Update the user's refresh token and last-login timestamp (write operation)

These have different EF Core tracking requirements.

## Decision
Use a two-fetch pattern:
1. **Untracked fetch**: Load the user without change tracking to validate credentials. This avoids the overhead of tracking an entity that may not need mutation (e.g., invalid password).
2. **Tracked re-fetch**: If credentials are valid, load the user again with change tracking enabled to update refresh tokens and persist changes.

## Rationale
- **Performance on failure path**: Invalid login attempts (the majority of auth traffic in adversarial scenarios) don't pay the cost of change tracking.
- **Explicit intent**: The two fetches clearly separate the validation phase from the mutation phase, making the code easier to reason about.
- **EF Core behavior**: Attaching an untracked entity for modification requires careful state management (`Attach` + manual `IsModified` flags). The re-fetch is simpler and less error-prone.

## Trade-offs
- Two database round trips on successful login. In practice, the first query is cached by the database engine's buffer pool, making the second near-instantaneous.
- Slight race condition window between the two fetches (user could be deleted between them). Acceptable because the second fetch returns NotFound, which is the correct outcome.

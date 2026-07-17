# ADR-047: Runtime Revocation of Soft-Deleted Users' Active Sessions

## Status
Accepted (2026-07-15).

## Context
Soft-delete is the framework's default deletion model (ADR-005): `AuditableBaseEntity.Delete()` sets
`IsDeleted = true` and EF global query filters hide the row, but the record survives for audit,
referential integrity, and undelete. Deactivating a user account is therefore a soft-delete, not a
row removal.

Authentication is stateless JWT (ADR-004): Identity mints an access token, and every service
validates it by signature and expiry without a per-request lookup against the account store. That is
exactly what makes it scale, and it is also the problem here. A JWT is a bearer credential that stays
valid until it expires; nothing in signature validation asks "is this account still active?" So when
an administrator soft-deletes a user, that user's already-issued access token keeps passing
validation on every service until it expires on its own. The same gap applies to the SSR session
cookie that carries the JWT for fresh browser GETs (ADR-022): the cookie handler populates
`HttpContext.User` from a token the API already issued, so a deleted user's prerender path also stays
authenticated until the token lifetime runs out.

Closing that gap the textbook way (a token deny-list or a per-request account-status check) reintroduces
exactly the stateful, per-request store lookup that stateless JWT was chosen to avoid. We wanted the
deletion to take effect quickly without paying a database round trip on every authenticated request,
and without coupling every extracted service to the Identity database.

## Decision
Add a shared-pipeline middleware, `SoftDeletedUserMiddleware`
(`Source/Presentation/MMCA.Common.API/Middleware/SoftDeletedUserMiddleware.cs:15`, BR-133), that
rejects an authenticated caller with HTTP 401 once the caller's account has been soft-deleted, backed
by a short cache so the account-status lookup is not paid on every request.

- **It runs after authentication, before authorization.** `UseCommonMiddlewarePipeline` registers it
  at `Source/Presentation/MMCA.Common.API/Startup/WebApplicationExtensions.cs:102`, immediately after
  `UseAuthentication` / `UseRateLimiter`
  (`WebApplicationExtensions.cs:96,101`) and before `UseAuthorization`
  (`WebApplicationExtensions.cs:103`), so `HttpContext.User` is already populated and the check gates
  every downstream endpoint.
- **Anonymous requests pass straight through.** When `ICurrentUserService.UserId` is null the
  middleware calls the next delegate and returns without any lookup
  (`SoftDeletedUserMiddleware.cs:43-51`), so unauthenticated traffic pays nothing.
- **The account-status check is an abstraction the Identity module implements.**
  `ISoftDeletedUserValidator`
  (`Source/Core/MMCA.Common.Application/Interfaces/Infrastructure/ISoftDeletedUserValidator.cs:7`)
  exposes a single `IsUserSoftDeletedAsync(userId, ...)` method
  (`ISoftDeletedUserValidator.cs:15`). Both apps' Identity modules implement it with one
  filter-bypassing existence query,
  `repository.ExistsAsync(u => u.Id == userId && u.IsDeleted, ignoreQueryFilters: true, ...)`
  (ADC: `MMCA.ADC/Source/Modules/Identity/MMCA.ADC.Identity.Application/Users/SoftDeletedUserValidator.cs:10,21-24`;
  Store: `MMCA.Store/Source/Modules/Identity/MMCA.Store.Identity.Application/Users/SoftDeletedUserValidator.cs:10,21-24`),
  registered scoped in each Identity module's DI
  (`MMCA.ADC.Identity.Application/DependencyInjection.cs:33`,
  `MMCA.Store.Identity.Application/DependencyInjection.cs:40`). The query bypasses the soft-delete
  global query filter deliberately, because a plain read would hide the very row it needs to find.
- **A 30-second cache amortizes the lookup.** `CacheDuration` is
  `TimeSpan.FromSeconds(30)` (`SoftDeletedUserMiddleware.cs:17`). The middleware reads
  `user:deleted:{userId}` from `ICacheService` first (`SoftDeletedUserMiddleware.cs:63-66`): a cached
  `true` short-circuits to 401 with no database call (`SoftDeletedUserMiddleware.cs:66-71`); a cache
  miss runs the validator once, caches the boolean for 30 seconds, and 401s if deleted
  (`SoftDeletedUserMiddleware.cs:73-84`); a cached `false` falls through to the next delegate
  (`SoftDeletedUserMiddleware.cs:86`). So a given user costs at most one status query per 30-second
  window per cache scope, not one per request.
- **It no-ops in services that do not host Identity.** The validator is resolved lazily via
  `context.RequestServices.GetService<ISoftDeletedUserValidator>()`
  (`SoftDeletedUserMiddleware.cs:53`) rather than as an `InvokeAsync` parameter. In an extracted
  service that does not host Identity (for example Store's Catalog or Sales service), no
  implementation is registered, so the middleware passes the request through
  (`SoftDeletedUserMiddleware.cs:54-61`): Identity is the source of truth and already validated the
  token upstream. Resolving it as a constructor/parameter dependency would instead 500 every request
  in those services. MMCA.Helpdesk wires the same pipeline
  (`MMCA.Helpdesk/Source/Hosts/MMCA.Helpdesk.Web/Program.cs:96`) but hosts only a Tickets module and
  registers no validator, so it takes the same no-op path.

`SoftDeletedUserMiddlewareTests`
(`MMCA.Common/Tests/Presentation/MMCA.Common.API.Tests/Middleware/SoftDeletedUserMiddlewareTests.cs`)
covers the branches: anonymous pass-through, no-validator pass-through with no cache call, a live
non-deleted pass, a live deleted 401, a cached-deleted 401 with no database call, and a cached
non-deleted pass with no database call.

The effect is a **bounded revocation window**, not instant revocation: after an account is
soft-deleted, its still-valid tokens keep working only until the cached status expires (at most the
30-second cache lifetime once the account has been queried at least once in that window), instead of
until the token itself expires.

## Rationale
- **Bounds the stateless-JWT revocation gap cheaply.** Stateless JWT (ADR-004) has no built-in
  revocation, so a deactivated account would otherwise stay usable for the full remaining token
  lifetime. A 30-second cached check turns "valid until the token expires" into "rejected within
  about 30 seconds," which is the point of the middleware.
- **The cache is what keeps it stateless-friendly.** Checking account status on every request would
  put a database read back in the hot path of every authenticated call, the cost stateless JWT was
  meant to avoid. Caching the boolean for 30 seconds keeps the lookup rate at most once per user per
  window, so the common case stays a cache hit.
- **The validator abstraction keeps the middleware in the framework.** The middleware lives in
  `MMCA.Common.API` and depends only on `ISoftDeletedUserValidator`, so it needs no reference to any
  app's `User` entity; each Identity module supplies the query against its own store.
- **Lazy resolution is what makes it safe everywhere.** One pipeline runs in Identity-hosting and
  non-Identity hosts alike; resolving the validator lazily lets the same middleware gate real
  requests where Identity lives and stay inert where it does not, without a per-host pipeline
  variant.

## Trade-offs
- **Revocation is bounded, not immediate.** A soft-deleted user whose status is cached as not-deleted
  keeps passing until that cache entry expires (up to 30 seconds). Shrinking the window costs more
  database lookups; lengthening it widens the exposure. 30 seconds is the chosen balance, and it is a
  compile-time constant (`SoftDeletedUserMiddleware.cs:17`), not configurable per host today.
- **The no-op posture trusts upstream validation.** In a service with no validator registered, a
  soft-deleted user's token is accepted for its full lifetime at that service, on the assumption that
  Identity is the source of truth and the token was validated upstream. Only Identity-hosting hosts
  actually enforce the revocation; extracted non-Identity services do not re-check.
- **Cache-scope-dependent.** The window is per cache scope: with a distributed cache the revocation
  is shared across replicas, but with a per-instance memory cache each instance carries its own
  30-second window, so the effective revocation lag is per replica.
- **Enforcement depends on registration.** An Identity-hosting service that fails to register
  `ISoftDeletedUserValidator` silently degrades to the no-op path, the same audit-the-inventory
  caveat other opt-in framework capabilities carry (ADR-005).

## Related
ADR-005 (soft-delete is the deletion model whose still-authenticated tokens this middleware revokes;
deleting a user is a soft-delete, not a row removal), ADR-004 (the stateless RS256/JWKS validation
that has no built-in revocation, which this bounds without a per-request store lookup), ADR-022 (the
SSR session cookie carries the same JWT into the prerender path this middleware also gates).

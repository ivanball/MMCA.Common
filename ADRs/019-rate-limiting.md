# ADR-019: Layered Rate Limiting with an Authenticated-Only Global Limiter

## Status
Accepted.

## Context
Every service exposes read and write endpoints to the public internet through the gateway (ADR-008).
Abusive or runaway clients (scrapers, credential stuffing, retry storms, a buggy SPA stuck in a loop)
can exhaust a service's threads, database connections, and downstream quotas. ASP.NET Core ships a
rate-limiting middleware, but "turn on rate limiting" is not the decision: the load-bearing questions
are *who* gets limited, *by what partition key*, and *what is exempt*. A naive per-IP global limiter
is actively wrong for this deployment for three reasons:

- Public read endpoints are output-cached, so legitimate anonymous browsing should be cheap, not
  throttled.
- Anonymous server-rendered (Blazor Server) traffic all shares the UI host's outbound IP, so an IP
  partition would throttle every public visitor as if they were a single abuser.
- Login and registration brute-force is a distinct threat with a distinct control (account lockout
  and per-IP registration throttling), not a general request cap.

## Decision
Rate limiting is **layered**, and the always-on global limiter is **authenticated-only**.

1. **A global limiter that only caps authenticated callers.** `AddCommonRateLimiting`
   (`MMCA.Common.API`) installs a `GlobalLimiter` (active on every request through `UseRateLimiter`)
   that:
   - **Exempts infrastructure traffic** outright (`NoLimiter`): `/health`, `/alive`, JWKS / OIDC
     discovery (`/.well-known/*`), and gRPC inter-service calls (`application/grpc` content type).
     These are legitimately high-frequency.
   - **Exempts anonymous traffic** (`NoLimiter`): unauthenticated requests are not counted, for the
     three reasons above.
   - **Caps each authenticated caller** to `globalPermitLimit` (default 300) requests per fixed
     one-minute window, partitioned by identity name, then the `user_id` claim, then remote IP,
     rejecting overage with `429 Too Many Requests`.
2. **Anonymous abuse is handled by the right-shaped control, not the global limiter.** Public reads
   are served from the output cache (`UseOutputCache`; ADC's Conference service defines
   `EventsCache` / `CategoriesCache` / `QuestionsCache` / `RoomsCache` policies on its public
   controllers), and login/registration brute-force is handled by `LoginProtectionService`
   (exponential-backoff account lockout after `MaxFailedAttempts` failed logins, plus per-IP
   registration throttling).
3. **Named policies remain for opt-in, per-endpoint tightening.** `AddCommonRateLimiting` also
   registers named limiters (`FixedPolicy`, `UserPolicy`) that a specific action can apply with
   `[EnableRateLimiting(...)]` when it needs a tighter cap than the global default. Nothing applies
   them by default.

## Rationale
- **Limit the traffic that is both attributable and expensive.** An authenticated request is tied to
  a principal and usually drives the database; capping per-principal stops a single account from
  monopolizing a service without punishing the public read path.
- **Do not punish shared-origin anonymous traffic.** With Blazor Server fronting public browsing
  behind one IP, and public reads served from the output cache, an anonymous IP cap would throttle
  legitimate visitors at scale while barely protecting an already-cached backend.
- **Right control per threat.** Brute-force is an auth concern with a lockout control; general
  overload is a per-user request cap; infrastructure endpoints must never be throttled. A single
  global IP bucket conflates all three.

## Trade-offs
- **The global limiter only protects the authenticated surface.** Anonymous endpoints rely entirely
  on output caching plus the login-protection service for abuse resistance; an uncached anonymous
  endpoint added later would have no global cap and must opt into a named policy or its own control.
- **Per-user partitioning depends on the authenticated principal being populated** when the limiter
  partitions the request, so the limiter's placement relative to authentication in the request
  pipeline is load-bearing: move it and the partition sees a different (or empty) principal.
- **Defaults are deployment-agnostic.** 300 requests/min/user is a coarse backstop, not a tuned SLO;
  a service with heavier legitimate per-user traffic must raise it, and a stricter endpoint must opt
  into a named policy.
- **In-process counters.** Limiter state is per-instance, so across N replicas the effective ceiling
  is roughly N times the configured limit. This is an accepted backstop, not a distributed quota.

## Related
ADR-004 (the JWKS/discovery traffic the limiter exempts, and the authenticated principal it keys on),
ADR-008 (the gateway edge this protects), ADR-017 (request idempotency, the other inbound-edge
safeguard against client retries).

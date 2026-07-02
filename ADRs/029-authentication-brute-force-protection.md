# ADR-029: Authentication Brute-Force Protection and Registration Throttling

## Status
Accepted (2026-06-27). Updated 2026-07-02 (the check/increment/reset call sequence was hoisted
into `AuthenticationServiceBase<TUser>`; the adoption note and the "convention the consumer must
call" trade-off were rewritten to match).

## Context
ADR-019's global rate limiter is **authenticated-only**: it caps requests per authenticated principal
and deliberately *exempts* anonymous traffic. That leaves the highest-value anonymous attack surface —
the login and registration endpoints — uncovered by the limiter (credential stuffing, password
spraying, registration spam). Two of those defences also cannot live in a per-principal limiter at all:
at login time there is **no principal yet**, so account lockout must key on the *submitted* identity
(email) and the client IP, not on an authenticated user. We needed a small, always-available service
that the Identity flow calls to throttle these pre-authentication paths.

## Decision
Provide a framework `ILoginProtectionService` (`MMCA.Common.Application.Auth`) with a single
implementation `LoginProtectionService` (`MMCA.Common.Infrastructure.Auth`), registered unconditionally
by `AddInfrastructure` (`services.TryAddScoped<ILoginProtectionService, LoginProtectionService>()`), so
every host that wires infrastructure has it. Its state lives in `ICacheService` (ADR-026), never in a
table.

- **Login lockout (email-keyed).** `IncrementFailedAttemptsAsync(email)` counts consecutive failures in
  a window (`FailedAttemptWindowMinutes`, default 30). Once `MaxFailedAttempts` (default 5) is reached it
  writes a lockout key with **exponential backoff** `min(1 << excessAttempts, MaxLockoutSeconds)` (cap
  default 300s). `CheckLockoutAsync(email)` returns `Result.Failure(Error.Unauthorized(
  "Auth.TooManyAttempts", …))` while locked, and `ResetFailedAttemptsAsync(email)` clears both the
  attempt and lockout keys on a successful login.
- **Registration throttle (IP-keyed).** `CheckRegistrationRateLimitAsync(ip)` fails with
  `Error.Unauthorized("Auth.RegistrationRateLimitExceeded", …)` once `MaxRegistrationsPerIpPerHour`
  (default 10) registrations from one IP land inside `RegistrationRateLimitWindowMinutes` (default 60);
  `IncrementRegistrationCountAsync(ip)` bumps the per-IP counter. A missing/empty IP is a deliberate
  **no-op (fail-open)**.
- **Keyed by submitted email / client IP, not by principal**, so it works before authentication — the
  gap a per-principal limiter cannot fill.
- **Counters are cache-scoped and TTL-bounded.** They live in the same swappable `ICacheService`
  substrate as ADR-026 (in-process memory in the monolith, distributed/Redis when wired) and self-expire
  via cache TTL — a lockout is inherently ephemeral, so expiry *is* the reset.
- **Returns `Result` (ADR-013)**, so the HTTP edge maps every failure to a uniform `401` without the
  endpoint special-casing it.
- **Centralized in the shared authentication base.** The call sequence lives once in
  `AuthenticationServiceBase<TUser>` (`MMCA.Common.Application.Auth`): `CheckLockoutAsync` before
  credential validation, `IncrementFailedAttemptsAsync` on each failed attempt,
  `ResetFailedAttemptsAsync` on a successful login, and `CheckRegistrationRateLimitAsync` /
  `IncrementRegistrationCountAsync` around sign-up. Store and ADC `AuthenticationService` are sealed
  subclasses that inject `ILoginProtectionService` into the base constructor and inherit those calls;
  neither app invokes the protection methods directly. Settings bind from the `"LoginProtection"`
  section.

## Rationale
- **Complements ADR-019 rather than duplicating it.** The global limiter protects authenticated
  *throughput* per principal; this protects the anonymous *auth surface* that limiter exempts. The
  threat and the partition key differ (per-principal request flood vs. per-identity credential guessing
  and per-IP signup spam), so they are two mechanisms by design, not one.
- **Cache-backed, no new table.** Reusing ADR-026's substrate means the protection scales from monolith
  to distributed with no schema and no per-handler branching, and a lockout's natural lifetime is a TTL,
  not a row to clean up.
- **Exponential backoff** frustrates automated guessing (each excess attempt doubles the wait) while a
  legitimate user's brief lockout self-heals within the cap.

## Trade-offs
- **Cache-scoped state weakens under scale-out without Redis.** In memory mode the counters are
  per-replica and evaporate on restart, so a multi-replica deployment that did not wire a distributed
  cache does not aggregate an attacker hitting different replicas. The answer is the same as ADR-026:
  register a distributed cache once scaled out (both apps do).
- **Email-keyed lockout is a targeted-DoS lever.** An attacker can lock a *known* account out by
  deliberately failing its logins. The short backoff cap (default 300s) and the generic 401 bound the
  harm, but it is an accepted availability-for-security trade.
- **IP-keyed registration throttle is coarse.** Shared NAT/proxy IPs throttle innocents together, and
  per-attacker IP rotation evades it; it is fail-open on a missing IP. It raises the cost of bulk signup,
  it does not stop a determined distributed attacker.
- **Protection rides on the shared base class, not on the HTTP edge.** Because the call sequence is
  centralized in `AuthenticationServiceBase<TUser>`, a consumer whose `AuthenticationService`
  subclasses it inherits the lockout and registration-throttle checks automatically (both apps do), so
  it is no longer a per-flow convention that a subclass can forget. What the framework still does not do
  is intercept the HTTP endpoints: an Identity flow written *without* the base class (calling
  `ILoginProtectionService` by hand, or not at all) remains unprotected. That residual is the same
  audit-the-inventory caveat as the other opt-in capabilities (ADR-019/020/021/026).

## Related
ADR-019 (the authenticated-only global rate limiter that exempts this anonymous surface),
ADR-026 (the `ICacheService` substrate these counters live in),
ADR-013 (the `Result` / `Error` the checks return),
ADR-022 (the browser session-cookie auth flow these endpoints sit behind).

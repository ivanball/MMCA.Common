# ADR-040: Authenticated output caching for public reads

## Status

Accepted (2026-07-10). Amended (2026-07-10): explicit query-string variance parity with the
built-in default policy (the initial release accidentally dropped it, collapsing every query
variant of a path onto one cache entry), plus an opt-in `bypassRoles` escape hatch for endpoints
whose payload is elevated for one privileged role.

## Context

The framework's read-scaling design leans on ASP.NET Core output caching: anonymous-readable
endpoints (`[AllowAnonymous]` GETs like event/session/speaker catalogs) carry named 5-minute
policies with tag-based eviction, primed by startup warmup and load-tested by k6.

That design was silently inert for the traffic that matters. The shared UI HttpClient pipeline
attaches the stored Bearer token to every outgoing API request via `AuthDelegatingHandler`,
including reads of public endpoints whose payload is identical for every caller. ASP.NET Core's
built-in default output-cache policy refuses both cache lookup and cache storage for any request
carrying an `Authorization` header (or an authenticated identity). The result: every logged-in
user bypassed the output cache on every read, and on conference day (when every attendee is
logged in) 100% of agenda/session/speaker reads landed on Basic-tier SQL. The gap was invisible
in load evidence because the k6 scripts and the warmup requests are anonymous, which is exactly
the traffic slice the default policy still cached.

Two ways out were considered:

1. Route public reads through a second, unauthenticated named HttpClient in the UI.
2. Replace the default policy server-side for the affected endpoints.

Option 1 bifurcates the UI's HTTP stack (two clients, two resilience pipelines, per-call-site
decisions that silently regress when someone picks the wrong client) and still leaves any other
authenticated caller (mobile hosts, cross-service calls, curl with a token) uncached.

## Decision

`MMCA.Common.API` ships `PublicEndpointOutputCachePolicy`, an `IOutputCachePolicy` that mirrors
the built-in default policy with one deliberate difference: it does not disable cache lookup or
storage when the request carries an `Authorization` header or an authenticated identity. It
enforces the same response-side guards (GET/HEAD only; never store `Set-Cookie` responses or
non-200s), varies the cache key by every query-string parameter (`CacheVaryByRules.QueryKeys =
"*"`, the same rule as the default policy; a raw `IOutputCachePolicy` registration replaces the
whole default chain, so the policy must restate it), and takes the expiration and eviction tags
as constructor arguments.

Hosts register it per named policy via the `OutputCacheOptions.AddPublicEndpointPolicy(name,
expiration, tags)` extension and reference it from `[OutputCache(PolicyName = ...)]` exactly like
any built-in policy. Tag-based eviction from mutating commands is unchanged.

The contract for applying it is strict: ONLY endpoints that are `[AllowAnonymous]` AND whose
response does not vary by caller identity. A cached response is served verbatim to every
subsequent caller, so a user-dependent payload behind this policy is an information-disclosure
bug, not a perf tweak.

One bounded relaxation exists for role-elevated payloads: the `AddPublicEndpointPolicy(name,
expiration, bypassRoles, tags)` overload makes callers in a bypass role skip the cache entirely
(no lookup, no storage), so they always read fresh and their elevated responses are never stored.
Use it when the payload is identical for every caller EXCEPT one privileged role (e.g. ADC
organizers see unpublished rows per BR-108). Per-user payloads remain out of scope: bypass roles
handle role-shaped variance, not identity-shaped variance.

## Rationale

- The response payload, not the request's auth state, is what determines cacheability. For a
  user-independent payload, `Authorization` is noise; refusing to cache on it turns the whole
  read-scaling mechanism off for the real workload.
- A server-side policy fixes every caller (UI, MAUI hosts, service-to-service, tooling) at the
  single point that owns the endpoint's caching semantics, instead of asking every client to
  special-case its transport.
- Constructor-parameterized expiration/tags keep the policy self-contained and testable; it does
  not depend on builder-chain internals that assume the default policy runs first.

## Trade-offs

- Consumers must audit which named policies move to `AddPublicEndpointPolicy`. Policies on
  permission-gated endpoints (e.g. an organizer dashboard) must NOT move; if such an endpoint
  needs caching, that is a separate decision with per-user vary rules.
- The output-cache store remains per-replica in-memory unless a Redis-backed store is wired;
  under scale-out, tag eviction propagates only to the replica that handled the mutation. The
  bounded staleness window (policy expiration) is the accepted limit, consistent with the
  existing 5-minute TTLs.
- Cache hit rate becomes meaningful for authenticated load tests; k6 scripts that log in now
  exercise the same cache path as anonymous ones.

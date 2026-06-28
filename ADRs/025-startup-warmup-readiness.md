# ADR-025: Startup Warm-Up and Readiness Gating for Cold-Start Mitigation

## Status
Accepted (2026-06-27).

## Context
On the Azure Container Apps Consumption plan a replica that has been idle is CPU-throttled, and a
scale-from-zero or scaled-out replica starts cold. The first authenticated request on such a replica
pays costs that a warm replica does not: DNS resolution, TCP and TLS handshakes, the HTTP connection
pool warm-up, and the lazy fetch of the OIDC discovery document by the JwtBearer middleware. That fetch
can stretch past the client timeout, which is the textbook "first request fails, second succeeds"
symptom. The platform also routes traffic to a replica as soon as its readiness probe passes, so a
replica that is technically started but not yet warm gets live traffic it cannot serve cleanly. We need
a way to (a) pre-warm the expensive paths before the replica takes traffic and (b) hold the replica out
of rotation until that warm-up has had its chance, without letting a single stuck dependency keep a
replica permanently out of service.

## Decision
Ship a small warm-up subsystem in `MMCA.Common.Aspire`, wired into `AddServiceDefaults()` so every host
gets it.

- **A readiness gate that starts closed.** `WarmupReadinessGate` (singleton) begins not-ready;
  `WarmupReadinessHealthCheck` is registered tagged `ready` and reports `Unhealthy` until the gate opens.
  `MapDefaultEndpoints()` maps `/health/ready` to every non-`live` check, so while warm-up is running the
  replica's readiness endpoint reports not-ready and the platform keeps traffic off it. (`/alive` maps
  only the `live`-tagged self check, so liveness is unaffected and the container is not restarted.)
- **A background runner that opens the gate once warm-up has had its chance.** `WarmupHostedService`
  runs every registered `IWarmupTask` exactly once, in parallel, then opens the gate. Critically the gate
  is opened in a `finally`, so it **opens even if tasks fail**: a stuck dependency must not keep a replica
  out of rotation forever.
- **Per-task failure is logged, not fatal, and falls back to lazy retry.** Each task runs in isolation; a
  thrown task is caught and logged at warning level ("will retry lazily on first use"), so the missed
  warm-up simply happens on the first real request (absorbed by the Polly pipeline, ADR-009). The one
  exception that is rethrown is an `OperationCanceledException` during host shutdown, so stopping the host
  is not mistaken for a task failure.
- **A built-in task that pre-warms OIDC discovery.** `OpenIdConnectMetadataWarmupTask` fetches
  `{Authority}/.well-known/openid-configuration` over the shared `IHttpClientFactory`, warming DNS, TCP,
  TLS, and the connection pool (and the authority's own discovery cache). It no-ops when no
  `Authentication:JwtBearer:Authority` is configured. The JwtBearer middleware caches discovery
  separately, so its own first fetch still runs, but over a now-warm connection it completes in
  single-digit milliseconds.
- **Extensible per host.** `AddWarmupReadiness()` registers the gate, the runner, the readiness health
  check, and the built-in OIDC task; a host adds its own pre-fetches (output cache, reference data) with
  `AddWarmupTask<T>()`.

## Rationale
- **Keep cold replicas out of rotation, briefly.** Gating readiness on warm-up means the platform does
  not send a user request to a replica that is still doing its first handshakes, which is what turns a
  cold start into a visible 5xx.
- **Availability over strict warmth.** Opening the gate even when a task fails is the load-bearing choice:
  a warm-up that depends on a temporarily unreachable dependency would otherwise pin the replica
  out of service indefinitely. Falling back to lazy retry (covered by the resilience pipeline) trades a
  possibly-slow first request for guaranteed eventual availability.
- **Warm the path, not just the cache.** The OIDC fetch is the specific cold-start failure we saw; even
  though the middleware re-fetches, warming the network path removes the timeout-sized first hit. This is
  the active half of the same cold-start story ADR-004 references from the auth side.
- **Free for every host, cheap for the ones that do not need it.** Putting it in `AddServiceDefaults`
  makes it the default posture; the built-in task self-disables when there is no authority, so a host
  without JwtBearer pays nothing.

## Trade-offs
- **A replica can enter rotation not fully warm.** Because the gate opens on failure (or timeout), the
  first request to such a replica still pays the lazy cost; the resilience pipeline mitigates this but
  does not eliminate it. This is deliberate (availability over warmth) but means the gate is a
  best-effort warm-up signal, not a guarantee.
- **The gate does not surface a broken dependency.** Since it opens regardless, a persistently failing
  warm-up task is visible only in logs and (for dependencies that have their own health checks) through
  the separate untagged readiness checks, not through the warm-up gate itself.
- **Startup work on every host.** Every host runs the warm-up runner and the built-in task even if the
  benefit is marginal (the task no-ops without an authority, but the hosted service and gate still spin
  up).
- **Middleware cache is separate.** The warm-up warms the connection, not the JwtBearer
  `ConfigurationManager`'s discovery cache, so the very first authenticated request still triggers the
  middleware's own (now-fast) fetch; the optimization is on the network path, not on eliminating the
  fetch.

## Related
ADR-004 (the OIDC discovery document the built-in task pre-fetches, and the auth-side view of the same
cold-start), ADR-009 (the Polly resilience pipeline that absorbs the lazy retry when the gate opens
before a task succeeds), ADR-019 (the rate limiter that exempts `/health` and `/alive`, the endpoints
this gate drives).

# ADR-017: HTTP Request Idempotency via Client-Supplied Keys

## Status
Accepted

## Context
Write endpoints (POST / PUT / PATCH) are exposed to **client retries and double-submits**: a flaky
network, an impatient user double-clicking, or a resilience pipeline re-issuing a request can cause the
same logical operation to execute twice (e.g. two orders, two payments). The `Result` pattern (ADR-013)
and domain invariants stop *invalid* states, but they do not stop a *valid* operation from being applied
twice when the second call is a genuine duplicate of the first.

This is a different concern from the **handler idempotency** of ADR-003: that is about domain-event
*consumers* tolerating at-least-once delivery from the outbox. ADR-017 is about the **inbound HTTP edge**
deduplicating *client* requests before the use case runs at all.

## Decision
Provide opt-in, client-driven request idempotency as an MVC action filter in `MMCA.Common.API`.

- **Opt-in per action.** `[Idempotent]` (`IdempotentAttribute`, a `ServiceFilterAttribute` resolving
  `IdempotencyFilter` from DI) marks an action. Nothing is deduplicated unless the action declares it.
- **Client supplies the key.** The caller sends an `Idempotency-Key` header. If the header is absent or
  blank, the action runs normally with **no** deduplication — the key is the client's assertion that "two
  requests with this key are the same operation."
- **Cache-backed replay.** The first response (status code + serialized body) is stored via
  `ICacheService` under `idempotency:{key}` for a bounded window (default 24h, configurable via
  `IdempotencySettings.CacheExpirationHours`). `ICacheService` resolves to the distributed (Redis) store
  when the host wires one and otherwise to an in-process memory cache (ADR-026), so cross-instance /
  cross-restart replay holds only when a distributed backing is configured. A later request with the same
  key replays the cached response and adds an `X-Idempotent-Replay: true` header so clients can tell a
  replay from a fresh execution.
- **Concurrency-safe within an instance.** The filter uses a fast-path cache read (no lock), then a
  per-key `SemaphoreSlim` (double-check locking) so concurrent duplicates that arrive before the first
  completes are serialized rather than both executing. Per-key semaphores are removed once no waiters
  remain, so the lock table does not grow unbounded.
- **Only cache deterministic success shapes.** Only `ObjectResult` responses are cached; redirects, file
  results, and the like are not replayed.

## Rationale
- **Safety at the edge, not in every handler.** Deduplication lives in one filter, so a handler stays a
  thin use case (ADR-014) and does not grow ad-hoc "did I already do this?" checks.
- **Client owns the identity of an operation.** Only the caller knows that a retry is the *same*
  operation; a server-generated key cannot distinguish a retry from a legitimately-similar new request.
- **Distributed-cache backing (when configured)** makes replays work across instances and across a
  service restart within the window, matching the database-per-service / multi-host deployment (ADR-006,
  ADR-008); with the in-memory fallback (ADR-026) the replay is per-instance and lost on restart.
- **Invariant-friendly default.** Absent header ⇒ no behavior change, so adding `[Idempotent]` is a safe,
  additive annotation.

## Trade-offs
- **The in-process semaphore only serializes duplicates landing on the same instance.** Two simultaneous
  identical requests routed to *different* instances can both miss the cache and execute; a configured
  distributed cache makes the *replay* consistent afterward but does not provide cross-instance mutual
  exclusion. For
  exactly-once across instances, the operation itself must also be naturally idempotent or guarded by a
  unique constraint.
- **Bounded window.** Replays only work within the retention window (default 24h); a duplicate after
  expiry re-executes.
- **Response-shape coupling.** Only `ObjectResult` is cached, and the cached body is the serialized value,
  so an endpoint whose response depends on per-request state (other than the body) will replay the
  original, not a freshly-computed response.
- **Opt-in.** An action that should be idempotent but is missing `[Idempotent]` gets no protection — the
  same audit-the-inventory caveat as ADR-005's `IAnonymizable`.

## Related
ADR-003 (handler idempotency for outbox/event consumers, a distinct concern), ADR-013 (Result is the
response the filter caches/replays), ADR-014 (the filter keeps the handler thin), ADR-009 (the resilience
pipeline that re-issues requests is the main source of the duplicates this filter absorbs), ADR-026 (the
`ICacheService` substrate whose distributed-vs-memory backing determines cross-instance replay).

# ADR-026: Two-Tier Caching: a Swappable `ICacheService` Substrate plus an HTTP Output-Cache Edge

## Status
Accepted (2026-06-27).

## Context
The framework needs caching in two distinct places. Inside the application pipeline, query results
are memoized and invalidated on mutation (the Caching decorators of ADR-014, keyed by
`IQueryCacheable` / `ICacheInvalidating`), and a brute-force counter backs login protection
(ADR-019). At the HTTP edge, public/anonymous read endpoints want their responses served without
touching a handler at all. These two needs have different lifetimes, different invalidation models,
and different failure tolerances, so a single cache primitive does not fit both.

The application-pipeline need also has to survive the monolith-to-services move (ADR-006/008): the
same handler code runs in a single process (where an in-process cache is correct and cheapest) and in
a scaled-out container app (where each replica's private memory cache would drift and a shared store
is needed). We did not want handlers to know which deployment they are in.

This ADR records the caching decisions themselves. ADR-014 owns the decorator *ordering* and the
marker interfaces; this ADR owns the *substrate, the two tiers, and the invalidation semantics* they
sit on.

## Decision
Cache in two tiers, each with its own substrate.

### Tier 1: an application `ICacheService` substrate with a runtime memory-or-distributed swap
- **One abstraction.** `ICacheService` (`MMCA.Common.Application/Interfaces/ICacheService.cs`) exposes
  `GetAsync` / `SetAsync` / `RemoveAsync` / `RemoveByPrefixAsync`. Application code (the ADR-014
  Caching decorators, `LoginProtectionService`) depends only on this interface, never on a concrete
  cache or on Redis.
- **The backing store is chosen at startup, not in code.** `AddCaching()`
  (`MMCA.Common.Infrastructure/DependencyInjection.cs:150`, called from `AddInfrastructure`) registers
  `DistributedCacheService` when a real `IDistributedCache` is present (one that is not the in-memory
  `MemoryDistributedCache`, i.e. Aspire registered Redis), and otherwise `MemoryCacheService`. The
  monolith with no distributed cache gets in-process caching for free; a host that wires Redis gets the
  distributed store with no application-code change. This is the same "monolith now, scale or extract
  later" seam as `InProcessMessageBus` vs `BrokerMessageBus` (ADR-003/006/008).
- **Prefix invalidation, implemented per store.** `IMemoryCache` has no key-enumeration API, so
  `MemoryCacheService` tracks live keys in a `ConcurrentDictionary` (kept in sync by a post-eviction
  callback) to support `RemoveByPrefixAsync`. `DistributedCacheService` serializes values as UTF-8 JSON
  and, when an `IConnectionMultiplexer` is resolvable, enumerates matching Redis keys to delete them;
  when no multiplexer is registered it treats prefix removal as a no-op and relies on the TTL backstop
  below.
- **A short default TTL bounds staleness.** `CacheOptions.DefaultExpiration` is a 30-second absolute
  expiration; callers may override per entry. The short default means even a prefix invalidation that
  cannot reach every replica (memory mode, or distributed mode without a multiplexer) self-heals within
  seconds.

### Tier 2: an HTTP output-cache edge
- **The pipeline always enables it; policies are opt-in per host.** `MMCA.Common.API` calls
  `app.UseOutputCache()` in the shared middleware pipeline
  (`MMCA.Common.API/Startup/WebApplicationExtensions.cs:90`), but ships no policies. Each service
  registers its own `AddOutputCache(...)`: most declare a `NoCache` base policy (Identity, Sales,
  Engagement, Notification), while the read-heavy public services declare real cacheable policies. ADC
  Conference and Store Catalog are the adopters today, with named policies and `[OutputCache]` on their
  public read controllers.

## Rationale
- **One substrate, swapped by environment.** Keeping `ICacheService` as the only thing application code
  sees lets the deployment decide memory vs distributed. The auto-swap (presence of a real
  `IDistributedCache`) means there is no flag to forget and no per-handler branching.
- **Two tiers because the jobs differ.** Tier 1 memoizes handler results and invalidates them precisely
  on mutation (prefix/`ICacheInvalidating`). Tier 2 skips the handler entirely for anonymous/public
  reads at the HTTP edge, which is also the anonymous-traffic lever ADR-019 leans on (anonymous callers
  are exempt from the per-user rate limiter, so output caching absorbs that load instead). They are not
  unified by design: different keys, different lifetimes, different eviction.
- **TTL-bounded correctness over distributed-invalidation guarantees.** A short default TTL makes the
  cache eventually consistent cheaply, so the system stays correct-enough even where prefix invalidation
  is best-effort, rather than depending on a perfectly fan-out invalidation.

## Trade-offs
- **Memory mode is per-replica.** In the in-process store each replica caches independently; a
  scaled-out deployment that did not wire Redis would see cross-replica staleness bounded only by the
  TTL. The framework's answer is to register a distributed cache once scaled out (both apps do).
- **Distributed prefix invalidation is conditional.** `DistributedCacheService` can only scan-and-delete
  by prefix when an `IConnectionMultiplexer` is in the container. The services register
  `AddRedisDistributedCache` (which provides `IDistributedCache`), not `AddRedisClient` (which would
  provide the multiplexer), so prefix-based invalidation against Redis is currently a no-op and staleness
  is bounded by the 30s TTL rather than evicted on write. Single-key `RemoveAsync` is unaffected.
  Registering the Redis client (or a multiplexer) would close this; until then the TTL is the backstop.
- **Distributed mode pays serialization and a network hop.** Values cross the wire as JSON; large or
  hot objects cost more than the in-process path.
- **Output caching is opt-in per service.** A read-heavy service that forgets to register a real
  `AddOutputCache` policy gets no edge caching (the `NoCache` base is the safe default), the same
  audit-the-inventory caveat as other opt-in capabilities (ADR-019/020/021).

## Related
ADR-014 (the Caching decorators and `IQueryCacheable` / `ICacheInvalidating` markers that consume this
substrate), ADR-019 (output caching as the anonymous-traffic lever, and `LoginProtectionService` is
another `ICacheService` consumer), ADR-006 / ADR-008 (the same monolith-to-services swap seam this
substrate follows).

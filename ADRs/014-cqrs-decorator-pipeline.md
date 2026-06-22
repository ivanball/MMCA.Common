# ADR-014: CQRS Handlers with a Decorator Pipeline

## Status
Accepted

## Context
Commands and queries share cross-cutting concerns: validation, transactions, cache invalidation,
logging / timing, and feature gating. Putting that logic inside each handler scatters it, makes the
ordering between concerns implicit, and couples use-case logic to infrastructure. We wanted each
handler to hold only its use-case logic, with the cross-cutting concerns applied uniformly and in a
known, intentional order, regardless of whether the handler is invoked from REST, gRPC, or an
integration-event consumer.

## Decision
Use single-responsibility handlers behind a Scrutor-composed decorator pipeline.

- `ICommandHandler<TCommand, TResult>` and `IQueryHandler<TQuery, TResult>`
  (`MMCA.Common.Application`) are one handler per use case, each returning `Result` / `Result<T>`
  (ADR-013).
- Cross-cutting concerns are decorators registered with Scrutor `TryDecorate` in
  `AddApplicationDecorators()`. Because `TryDecorate` applies in **reverse** registration order (last
  registered is outermost), the execution order (outermost to innermost) is:
  - **Commands:** FeatureGate -> Logging -> Caching -> Validating -> Transactional -> Handler
  - **Queries:** FeatureGate -> Logging -> Caching -> Handler
  - plus an optional `Profiling` decorator when profiling is enabled.
- **The order is load-bearing and hard-coded** (not config-driven): validation runs *before* the
  transaction opens (an invalid command never touches the DB), cache invalidation runs *outside* the
  transaction boundary (a rolled-back command does not evict valid cache), logging wraps the whole
  pipeline to time it, and the feature gate short-circuits first.
- **Decoration is opt-in per concern** via marker interfaces: `ITransactional`, `ICacheInvalidating`
  (with `CachePrefix`), `IQueryCacheKeyProvider`. A handler that implements none simply skips that
  decorator's work, so messages pay only for the concerns they declare.
- Handlers, validators, and mappers are auto-registered by convention
  (`ScanModuleApplicationServices<TMarker>()`); `AddApplicationDecorators()` MUST be called **last**,
  after every module's concrete handlers exist, so `TryDecorate` can find them. This is the fixed DI
  sequence consumers follow (`AddApplication` -> `ScanModuleApplicationServices...` ->
  `AddApplicationDecorators` -> `AddInfrastructure` -> `AddAPI`).

## Rationale
- **Thin, testable handlers.** A handler has no transaction, logging, or caching plumbing, so it is
  unit-tested in isolation.
- **One place to read and change the pipeline.** The order is explicit and intentional, documented
  inline at the registration site, not emergent from scattered code.
- **Transport-agnostic.** The pipeline runs around the handler itself, so REST, gRPC, and event
  consumers all get the same behavior; HTTP middleware would only cover the REST path.

## Trade-offs
- **Registration order is the reverse of execution order** (a Scrutor foot-gun), mitigated by the
  inline ordering comments in `AddApplicationDecorators()`.
- Decorators must be registered after handlers, so the DI sequence is a constraint consumers cannot
  reorder freely.
- A new cross-cutting concern means a new decorator inserted at the correct depth; placing it wrong can
  silently change semantics (for example, validating *inside* the transaction).

## Related
ADR-013 (Result, the short-circuit currency of the pipeline), ADR-003 (handlers raise domain events
that the outbox drains after `SaveChanges`).

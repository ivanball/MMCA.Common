# Changelog

All notable changes to the MMCA.Common packages are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow [Semantic Versioning](https://semver.org/)
and are derived from git tags by MinVer (see [the published versioning policy](https://ivanball.github.io/docs/guides/common-VERSIONING.html)).

## [Unreleased]

## [1.125.0] - 2026-07-24

Correctness release from a review of the MMCA.Common and MMCA.ADC codebases, plus additive feature
work extracted from the MMCA.Store Sales consistency-guards PR (Store #39). No public API is removed,
but **three fixes change behavior at the API edge** and are called out under Changed below: the
ownership filter now denies by default, unparseable filter values are rejected instead of ignored,
and only successful responses are cached for idempotent replay.

### Security
- **Login lockout could be bypassed by changing an email's capitalization (ADR-029).**
  `LoginProtectionService` built its `login:lockout:` and `login:attempts:` keys from the raw request
  string while the user lookup resolved through the `Email` value object (trim + lowercase), so
  `User@x.com`, `user@x.com`, and a padded variant targeted one account but got three independent
  counters and lockouts: cycling capitalization reset the exponential backoff indefinitely. Keys now
  route through the same normalization, with a trim-and-lowercase fallback for malformed addresses
  (which never match a user but still increment a counter).
- **Idempotency keys were global, so one caller's response could be replayed to another (ADR-017).**
  The cache key was `idempotency:{client-supplied header}` and nothing else, so two callers choosing
  the same value shared an entry and one user's serialized response body was served to the other;
  with services sharing a cache instance the collision also crossed endpoints and services. The key
  is now `SHA-256(subject | method | route template | client key)`, where subject is the `user_id`
  claim or `anon:{remote address}`.
- **The ownership filter failed open (ADR-033).** `OwnerOrAdminFilter` called `next()` whenever the
  owner parameter could not be resolved (absent, non-int, or inside a bound model), so it silently
  stopped guarding any action whose parameter was optional or non-integer. It now denies by default;
  see Changed.

### Fixed
- **Domain events raised by an in-process handler were silently discarded (ADR-003).** The post-dispatch
  cleanup cleared each aggregate's event list wholesale, which also wiped anything a handler raised on
  that same aggregate during dispatch: those events arrived after the capture and were removed before
  any later capture could see them, so they never dispatched and never reached the outbox. Capture now
  snapshots each aggregate's events and removes exactly those, via the new
  `IAggregateRoot.RemoveDomainEvents`.
- **An execution-strategy retry duplicated inserts and outbox rows (ADR-003).**
  `ExecuteInTransactionAsync` re-runs its delegate against the same cached `DbContext` instances, so
  entities added by a failed attempt were still `Added` and were inserted again, and because capture
  runs on every `SavingChanges` pass while events are cleared only after a successful save, each
  attempt appended another outbox row per event: one transient SQL failure published every integration
  event twice. Retries now reset the change tracker, and an abandoned capture's staged rows are
  discarded.
- **A save could throw "Collection was modified".** `DbContextFactory` enumerated its context
  dictionary while each save dispatched domain events in-process; a handler reaching a
  not-yet-materialized data source calls `GetDbContext`, which writes into that dictionary mid-loop.
  Every enumeration now works from a snapshot, and `SaveChangesAsync` re-loops (bounded) so a context
  materialized during the save is still saved rather than skipped.
- **Prefix cache invalidation could miss a live entry (ADR-026).** `MemoryCacheService` removed a key
  from its tracking table on *every* eviction reason, but `IMemoryCache` queues post-eviction callbacks
  to the thread pool, so overwriting a live key could delete the tracking record for the entry that
  just replaced it. The entry stayed cached but invisible to `RemoveByPrefixAsync`, clearable only by
  its TTL. The callback now skips `EvictionReason.Replaced`.
- **A renamed filter property was validated and then silently dropped (ADR-034).** `ApplyFilters` fell
  back to the DTO property name while `ValidateFilters` fell back to the mapped entity name, so a plain
  `DTOToEntityPropertyMap` rename passed validation and was then skipped, returning an *unfiltered*
  result set with a 200. Both now share one resolver.
- **Pagination edges.** A page number near `int.MaxValue` overflowed the checked 32-bit Skip offset into
  a 500 instead of the empty page it describes (now 64-bit and range-checked); an unpaginated read
  reported the 1000-row safety cap as `TotalItemCount`, claiming the set was exactly that size (now
  counts only when the materialized rows reach the cap); and `PaginationMetadata.PageSize` reported the
  requested size rather than the clamped one the pipeline applied.
- **`IsInRole` saw only the first role claim.** It compared against `ICurrentUserService.Role`, so a
  principal holding several roles failed the check for all but whichever was listed first. Latent while
  tokens carry one role, and it would have surfaced as a silent authorization denial. Added
  `ICurrentUserService.Roles` (default interface member) and redefined `IsInRole` over it.
- **The outbox processor burned retries on shutdown (ADR-003).** Its general `catch` also caught the
  cancellation raised at host shutdown, incrementing `RetryCount` and stamping `LastError` on the whole
  remainder of the batch, so a graceful restart could dead-letter messages never actually attempted.
- **The keyed by-id fast path was unreachable (ADR-034).** `IsPrimaryKeyOnlyLookup` treated `includeFKs`
  as disqualifying while `EntityControllerBase.GetByIdAsync` defaults it to `true`, so every REST by-id
  read fell through to the dynamic-filter pipeline (parsed string predicate, `TOP 1000`, client-side
  `FirstOrDefault`) where a keyed `TOP 1 WHERE Id = @id` would do.
- **The query-cache lock table could grow without bound.** Its comment claimed it was bounded by the set
  of distinct cache keys, which holds only for parameterless keys; any `CacheKey` embedding a user id or
  filter value grew it indefinitely. Both it and the idempotency lock now use the new striped lock.

### Added
- **`KeyedSemaphoreStripe` (Shared).** Fixed-width per-key lock. Replaces the one-semaphore-per-key
  dictionary, which forced a choice between two defects: removing the entry when the last holder
  releases lets one caller wait on a semaphore no longer in the table while a second creates a fresh one
  (both then execute, defeating the lock), and never removing it grows the table without bound.
- **`[AllowMissingOwner]` (API).** Explicit opt-out from the ownership filter's deny-by-default, for
  actions guarded another way (a row-scoping specification, or their own policy). The attribute is an
  assertion, so each application site must name the guard that replaces the check.
- **`ICacheService.IncrementAsync`.** Default interface member (no implementer breaks) with a Redis
  `INCR` override, for counters whose read-modify-write could lose concurrent increments.
- **`IFilterStrategy.CanParseValue`.** Default interface member returning `true`, implemented by the six
  value-type strategies, so custom strategies are unaffected until they opt in.
- **`Cache:KeyPrefix` (`CacheKeyPrefixOptions`).** Optional namespace for services sharing one cache
  instance. Deliberately applied inside `DistributedCacheService` rather than through
  `RedisCacheOptions.InstanceName`, which sits below this abstraction where `RemoveByPrefixAsync`'s SCAN
  cannot see it and would silently evict nothing.
- **`IWriteRepository.ExecuteUpdateAsync` set-based conditional update (Application + Infrastructure).**
  Symmetric counterpart to `ExecuteDeleteAsync`: one atomic `UPDATE ... SET ... WHERE ...` through the
  repository abstraction, intended for contention-proof conditional updates (stock decrements, quota
  claims) where zero rows affected means the guard did not hold and the database arbitrates races. The
  SET clause is described through the new EF-free `IUpdatePropertySetter<TEntity>` builder (fixed
  values or expressions over the current row), translated to EF Core `SetPropertyCalls` by the new
  `UpdatePropertySetterBuilder`. Global query filters (soft delete) apply to the WHERE; domain events
  are bypassed (as with `ExecuteDeleteAsync`); `LastModifiedOn`/`LastModifiedBy` are stamped
  automatically (TimeProvider clock + `ICurrentUserService` when available) unless set explicitly.
- **`ConcurrencyTokenRequest` (Shared).** Reusable request body for lifecycle/state-transition
  endpoints whose only payload is the ADR-035 optimistic-concurrency token: bind as an optional body
  (`EmptyBodyBehavior.Allow`) so body-less callers skip the stale-view check. Replaces per-app copies
  (Store's `OrderTransitionRequest`, ADC's lifecycle equivalents) at the next consumer sweep.
- **`PeriodicBackgroundService` (Infrastructure).** Base class for fixed-interval background sweeps:
  enablement gate, TimeProvider-driven startup delay + interval waits (deterministic in tests via
  `FakeTimeProvider`), and a failing cycle that is logged without killing the loop. For reconciliation
  and cleanup work (e.g. Store's stuck-payment sweep); deliberately not used by the signal-driven
  outbox processor.

### Changed
Three edge-visible behavior changes. Each replaces a fail-open default; review consumer endpoints
against them.

- **The ownership filter denies by default (ADR-033).** An action guarded by
  `[ServiceFilter(typeof(OwnerOrAdminFilter))]` whose owner parameter cannot be resolved is now
  rejected instead of allowed through. Because the filter is usually applied at controller level, this
  covers every action on that controller, including ones inherited from the entity controller bases.
  Audit them and mark the ones guarded another way with `[AllowMissingOwner]`.
- **An unparseable filter value is a 400 (ADR-034).** `?filter=id:equals:abc` previously returned the
  whole (capped) result set because the strategy could not parse the value and silently applied no
  predicate. Validation now rejects it with `Filter.Value.Invalid`.
- **Only 2xx responses are cached for idempotent replay (ADR-017).** Any `ObjectResult` used to be
  cached, including ProblemDetails failures, so a client retrying after a transient 500 kept receiving
  that 500 for the full 24-hour window instead of the retry executing.

### Notes
`IAggregateRoot` gains `RemoveDomainEvents`. The only implementer across all four repos is
`AuditableAggregateRootEntity`, so every aggregate inherits it and no consumer change is required.

### Added
- **`IWriteRepository.ExecuteUpdateAsync` set-based conditional update (Application + Infrastructure).**
  Symmetric counterpart to `ExecuteDeleteAsync`: one atomic `UPDATE ... SET ... WHERE ...` through the
  repository abstraction, intended for contention-proof conditional updates (stock decrements, quota
  claims) where zero rows affected means the guard did not hold and the database arbitrates races. The
  SET clause is described through the new EF-free `IUpdatePropertySetter<TEntity>` builder (fixed
  values or expressions over the current row), translated to EF Core `SetPropertyCalls` by the new
  `UpdatePropertySetterBuilder`. Global query filters (soft delete) apply to the WHERE; domain events
  are bypassed (as with `ExecuteDeleteAsync`); `LastModifiedOn`/`LastModifiedBy` are stamped
  automatically (TimeProvider clock + `ICurrentUserService` when available) unless set explicitly.
- **`ConcurrencyTokenRequest` (Shared).** Reusable request body for lifecycle/state-transition
  endpoints whose only payload is the ADR-035 optimistic-concurrency token: bind as an optional body
  (`EmptyBodyBehavior.Allow`) so body-less callers skip the stale-view check. Replaces per-app copies
  (Store's `OrderTransitionRequest`, ADC's lifecycle equivalents) at the next consumer sweep.
- **`PeriodicBackgroundService` (Infrastructure).** Base class for fixed-interval background sweeps:
  enablement gate, TimeProvider-driven startup delay + interval waits (deterministic in tests via
  `FakeTimeProvider`), and a failing cycle that is logged without killing the loop. For reconciliation
  and cleanup work (e.g. Store's stuck-payment sweep); deliberately not used by the signal-driven
  outbox processor.

## [1.124.0] - 2026-07-23

Maintenance release strengthening the architecture-test rule library. No breaking changes and no
public API removed; all existing factories in Common and the consumers already comply.

### Added
- **`DomainFactoriesReturnResult` fitness function (`MMCA.Common.Testing.Architecture`).**
  Generalizes the previously aggregate-only `Result<T>`-factory check to every `Create` factory on
  domain entities and value objects across the Domain and Shared layers, so a future factory that
  returns a bare value object or entity (bypassing invariant checks) fails the build. Wired into
  both `AggregateConventionTestsBase` and `EntityConventionTestsBase`, so Store, ADC, and Helpdesk
  inherit the rule through their existing subclasses (fitness methods 91 -> 93).

## [1.123.0] - 2026-07-22

Maintenance release removing a redundant integration-event publish abstraction. **Breaking:** the
`IIntegrationEventPublisher` interface and its `IntegrationEventPublisher` adapter are removed;
callers inject `IEventBus` directly. No runtime behavior changes.

### Removed
- **`IIntegrationEventPublisher` (Application) and `IntegrationEventPublisher` (Infrastructure).** The
  adapter delegated every call straight to `IEventBus.PublishAsync`, whose single-event overload has
  an identical signature, so it carried no behavior the interface below it did not already provide.
  Callers that injected `IIntegrationEventPublisher` now inject `IEventBus` directly (same
  outbox-persist-then-dispatch in the monolith, same broker swap via `AddBrokerMessaging`). Removing
  the adapter is the first step of the longer consolidation onto `IMessageBus` as the single outbox
  transport (see the events/outbox onboarding chapter).

## [1.122.0] - 2026-07-22

Feature release completing the dynamic-filter operator matrix, hardening pagination, and making
distributed cache invalidation observable. No breaking changes and no public API removed; every
existing filter, query, and cache behavior is preserved.

### Added
- **Dynamic-filter operator matrix completed.** `IS EMPTY` / `IS NOT EMPTY` null checks now work on
  `bool?`, `int?`, `decimal?`, and `Guid?` columns (previously only `DateTime?` could be filtered
  for null); `IN` is now supported by the decimal and DateTime strategies (parity with the existing
  int/Guid/string `IN`); and an inclusive `BETWEEN` range (`"min,max"`) is supported by the int,
  decimal, DateTime, and long strategies.
- **`LongFilterStrategy`.** `long` / `long?` properties are now registered in `QueryFilterService`
  by default (equality, comparison, `IN`, `BETWEEN`, and null checks). A `long`-keyed entity
  previously failed filter validation with "No filter strategy registered".

### Changed
- **Pagination backstop in `EntityQueryPipeline`.** The pipeline now clamps a request's page size to
  the framework ceiling (`MaxUnboundedResultLimit`) in both paginated paths, as defense in depth.
  The API-boundary `ApplicationSettings.MaxPageSize` clamp is unchanged; this guard means a direct
  Application-layer caller (a gRPC handler or cross-module call) that bypasses that boundary can no
  longer request an unbounded page.

### Fixed
- **Silent no-op cache invalidation is now observable.** When `DistributedCacheService` has no
  `IConnectionMultiplexer` (e.g. a SQL-Server-backed `IDistributedCache`, or Redis registered
  without a client), prefix eviction was a silent no-op. It now logs a warning (once for the
  steady-state missing-multiplexer case, every time for the anomalous no-server case) so a
  TTL-only-invalidation deployment is visible rather than invisible.
- **`MemoryCacheService.GetAsync<T>` no longer throws on a type-mismatched key.** A key reused under
  a different `T` now returns a clean cache miss instead of an `InvalidCastException`.

## [1.121.0] - 2026-07-21

Maintenance release: the C# 14 extension-block migration, the shared analyzer baseline with the
ConfigureAwait gate (ADR-049), and the 2026-07-20 NuGet audit sweep (SQLite advisory resolved, so
consumers can drop their matching suppressions at this pin sweep). No breaking changes and no
public API changes (the extension-block migration preserves the lowered static surface).

### Security
- **SQLite advisory GHSA-2m69-gcr7-jv3q (CVE-2025-6965) resolved.** `SQLitePCLRaw.bundle_e_sqlite3`
  now ships the patched native SQLite (pinned at 2.1.12, then bumped to 3.0.4) and is referenced
  directly by `MMCA.Common.Infrastructure` (same pattern as the MessagePack pin) so the fix flows
  to consumers through the published package graph. The `NuGetAuditSuppress` entry was removed and
  the accepted-advisory list is now empty (ADR-038 updated);
  `dotnet list --vulnerable --include-transitive` reports zero rows. Consumers can drop their own
  suppressions for this advisory when they take this version.

### Changed
- **C# 14 extension-block migration complete.** The 15 remaining classic `this T` extension classes
  (~40 methods) moved to `extension(T)` blocks, finishing the adoption started in the DI
  registration files. Methods stay methods, so the lowered static surface and binary compatibility
  with consumers are unchanged; internal `RuleHelpers` parameterless helpers became extension
  properties.
- **Shared analyzer baseline + ConfigureAwait gate (ADR-049).** `.editorconfig` restructured into a
  SHARED ANALYZER BASELINE region plus repo-specific deltas (workspace drift guard:
  `Tools/Scripts/compare-analyzer-config.ps1`); IDE0005 and S125 promoted to warning, CA1031 to
  suggestion; scoped per-glob rules replace repeated inline suppressions. CA2007 is now enforced
  for `Source/**` (UI component packages excluded): packaged non-UI code awaits with
  `ConfigureAwait(false)` (324-site sweep).
- **Dependency refresh (2026-07-20 audit + dependabot).** EF Core 10.0.10, OpenTelemetry 1.17,
  Azure.Identity 1.21, BenchmarkDotNet 0.15.8, MudBlazor 9.7, Microsoft.OpenApi 2.11.0,
  SQLitePCLRaw 3.0.4, Scalar.AspNetCore 2.16.16, Meziantou.Analyzer 3.0.124, plus other approved
  servicing bumps; MassTransit stays pinned to v8 by policy. CI: actions/setup-dotnet 5 -> 6;
  dependabot no longer rebases open PRs and ignores Microsoft.OpenApi majors.
- **Documentation library centralized in the Website repo.** ADRs, the rubric, scorecards,
  backlogs, and the narrative guides are canonical under `Website/docs-src/` (published at
  `https://ivanball.github.io/docs/`); `FACTS.md`, `CHANGELOG.md`, `SECURITY.md`,
  `NavigationFlow.md`, `CONTRIBUTING.md`, and the deployment sample doc stay in this repo.

## [1.120.0] - 2026-07-19

Correctness release from the 2026-07-19 full review: the event/transaction core, outbox
scale-out safety, and the previously-untested guarantees. Behavior changes below are
deliberate fixes; consumers must add one EF migration (two new nullable outbox columns and
filtered unique indexes) when adopting.

### Changed (behavior)
- **Transactional commands roll back on business failure** (`ITransactional` + a returned
  failed `Result`): previously the transaction committed, leaving partial writes when a
  handler saved and then failed a later invariant (ADR-014 revision).
- **In-process domain event dispatch is deferred until after commit** and dropped on
  rollback, so handler side effects never act on state that can still roll back and
  execution-strategy retries cannot double-dispatch.
- **Integration events raised via `AddDomainEvent` now route through the outbox to
  `IMessageBus`** (broker-correct); previously they were dispatched in-process and marked
  processed, silently never reaching the wire in extracted deployments (ADR-003 revision).
- **Sync `SaveChanges` is now symmetric**: captured events are cleared (previously a later
  async save re-captured them into duplicate outbox rows) and the audit user id is stamped.
- **Unique indexes on soft-deletable entities exclude deleted rows** via a new
  model-finalizing convention, so a soft-deleted row no longer blocks re-creating the same
  record; hand-authored index filters win. Consumers get index-altering migrations.
- **`QueryFieldService.ApplySorting` treats the DTO map plus real entity properties as a
  strict allowlist**: client-supplied sort strings can no longer reach Dynamic LINQ as
  nested paths or expressions.
- **`Result.Failure` with an empty error collection now throws `ArgumentException`**
  instead of fabricating a success carrying a null value.

### Added
- **Outbox lease/claim** (`OutboxMessage.LockedUntil` and `LockToken`, `Outbox:LeaseSeconds`):
  concurrent processor replicas can never double-dispatch; `minReplicas: 1` is now a cost
  choice, not a correctness requirement (ADR-030 note). Retry exhaustion emits an Error log
  and the `outbox.dead_letter.count` metric with `reason=retries_exhausted`;
  `Outbox:DeadLetterRetentionDays` keeps failed payloads longer than processed rows.
  `OutboxProcessor` accepts an injectable `TimeProvider`.
- **`ModuleLoader`**: explicit-assemblies `DiscoverAndRegister` overload (the AppDomain scan
  misses not-yet-loaded assemblies) and `ValidateRemoteDependencies(IServiceProvider)`, a
  startup check that every `RemoteDependencies` declaration actually resolves.
- **`MMCA.Common.Testing`**: `HandlerTestBase<THandler>` (the UnitOfWork/repository mock
  scaffold consumers copy-pasted per handler test) and `DecoratorPipelineOrderTestsBase`
  (asserts the ADR-014 nesting from a real built container). The package now depends on
  `MMCA.Common.Application` and Moq.
- **`MMCA.Common.Testing.Architecture`**: layer-map completeness facts on
  `LayerDependencyTestsBase` (a repo whose map omits a layer no longer passes vacuously);
  governance interfaces matched by full name; opt-in `HandlerResultConventionTestsBase`
  (handlers' `TResult` must be a `Result`) and `RawQueryableConventionTestsBase`
  (ban `IRepository.Table*` in Application code, with an allowlist ratchet).
- **`DataGridListPageBase.LoadFailed`**: pages can render a real inline error state instead
  of the indistinguishable "no records" empty state after a failed fetch.

### Fixed
- `SET IDENTITY_INSERT` is wrapped in try/finally: a failed save can no longer leave the
  flag on the pooled connection or strand hidden entities in the Unchanged state.
- Audit `CurrentSaveUserId` resets after every save, so an internal follow-up save cannot
  stamp rows with the previous caller's identity.
- Cache-stampede per-key lock no longer eagerly removes semaphores (a race could let two
  concurrent executions through); cross-instance protection documented as best-effort.

### CI
- New `package-consumption` job packs every package to a local feed and builds a throwaway
  consumer against the nupkgs, catching pack breaks and package-mode-only failures before a
  release. `--minimum-expected-tests` raised 1 to 2000; the GHSA suppression grep is scoped
  to actual `NuGetAuditSuppress` lines; the webkit pseudo-locale sentinel has a bounded
  in-test retry. Four Testing packages had their blanket NoWarn lists pruned (dead-code
  detectors re-enabled). Outbox tests run on `FakeTimeProvider` (Infrastructure test tier
  ~11s to ~3s).

## [1.118.0] - 2026-07-17

FinOps cost-knob release plus a dependency and analyzer refresh. No breaking changes and no API
changes; the two new telemetry knobs are opt-in (unset preserves current behavior).

### Added (2026-07-13 FinOps §31: metric-family cost knobs)
- **`Telemetry:DisableHttpClientMetrics` / `Telemetry:DisableRuntimeMetrics`** (`MMCA.Common.Aspire`,
  `ConfigureOpenTelemetry`, rubric §31): two opt-in boolean knobs that drop the two highest-volume,
  lowest-value OpenTelemetry metric families from export (HttpClient connection/request metrics and the
  .NET runtime `dotnet.*` metrics). On a low-traffic multi-service deployment these are ~85% of the
  `AppMetrics` data points and carry no end-user-visible signal; dropping them cut total Log Analytics
  ingestion ~70% on the MMCA apps. Unset (default) keeps both, so there is no behavior change for a host
  that does not opt in, and anything but a boolean `true` keeps the family (a typo cannot silently blind
  it). Server-side RED metrics (`http.server.*` / `aspnetcore.*` / `kestrel.*`) and `AppDependencies`
  traces are untouched. See `COST.md`.

### Security
- **AngleSharp pinned to 1.5.0 (CVE-2026-54570 / GHSA-pgww-w46g-26qg).** bUnit floors the transitive
  AngleSharp at 1.4.0, which carries a Moderate mXSS advisory (MathML `annotation-xml` handling). The two
  bUnit-referencing projects (`MMCA.Common.Testing.UI`, `MMCA.Common.UI.Tests`) now take a direct
  reference to the patched 1.5.0. Test-tier only; no production runtime surface.

### Changed
- **Dependency and analyzer refresh.** SixLabors.ImageSharp 3.1.11 -> 3.1.12, Meziantou.Analyzer
  and SonarAnalyzer.CSharp, OpenTelemetry.Api and OpenTelemetry.Instrumentation.Runtime, plus the CI
  action pins (checkout, upload-artifact, download-artifact). No public API changes.
- **ADR and scorecard refresh.** update-adrs drift fixes (ADR-036, ADR-041, ADR-042, ADR-045) and the
  twentieth-wave ArchitectureScorecard re-score with backlog reconciliation.

## [1.117.0] - 2026-07-16

Scorecard-uplift wave release: four new CI enforcement gates, the child-entity optimistic-concurrency
overload (ADR-035 amendment), and the Secondary brand-token single-sourcing. No breaking changes;
one behavior change consumers should note (the notification routes now carry `[Authorize]`).

### Added
- **Child-entity `SetOriginalRowVersion` overload (ADR-035 amendment, rubric §8).** New
  `MMCA.Common.Domain.Interfaces.IRowVersioned` (implemented by `AuditableBaseEntity<TId>`) lets
  `IWriteRepository.SetOriginalRowVersion(IRowVersioned childEntity, byte[]? rowVersion)` stamp a
  tracked CHILD entity's original concurrency token (e.g. a `ProductVariant` under a `Product`),
  with the same null-or-empty no-op contract as the aggregate-typed overload. Update handlers that
  mutate children through the aggregate's repository call it per child after loading.
- **Secondary brand tokens (rubric §20).** `BrandColors` gains `Secondary`/`SecondaryDark`/
  `SecondaryLight` (values unchanged: the palette previously hard-coded the same hex), `app.css`
  gains `--mmca-secondary`/`--mmca-secondary-dark`, both `MMCATheme` palettes source from the
  constants, and `BrandColorTokenTests` guards the new tokens plus palette sourcing.
- **`NavigationContractTests` (rubric §25).** Arch-tier drift gate asserting `NavigationFlow.md`'s
  routes/auth table matches the `RouteAttribute`/`AuthorizeAttribute` reality of `MMCA.Common.UI`
  (set-equality both ways, auth-posture consistency, non-vacuity floor).
- **Latency-regression gate (rubric §12).** The `performance-smoke` CI job now measures
  (`--job Short`, JSON export) and a new dependency-free `build/perfgate` verifier fails it against
  the committed `Tests/Performance/perf-baseline.json` (deterministic allocation ceilings + a
  1000x compiled-expression-cache ratio floor).
- **`sample-deployment-validate` CI job (rubric §17).** Compiles both `samples/deployment` Bicep
  templates on every push/PR so the reference IaC cannot rot silently.

### Changed
- **The notification routes are now guarded (rubric §25).** `/notifications`,
  `/notifications/inbox`, and `/notifications/send` carry `@attribute [Authorize]`, matching the
  documented contract (previously the routes were open and only the APIs enforced auth); an
  anonymous visit now redirects to `/login`. The send page's role/claim gate remains
  consumer-declared (NavItem filter) plus server-side API authorization.
- **The `/counter` template-leftover page was removed** (routable but unreferenced and
  undocumented), along with its orphaned resx pairs.
- **CI gates promoted to required (rubric §22/§33):** the webkit `ui-e2e` leg (11 consecutive green
  main runs) and the `consumer-source-build` Helpdesk canary (9 consecutive green runs) lost their
  `continue-on-error` and joined branch protection.
- **The Store-specific `.cart-drawer` responsive width tiers left `app.css`** (consumer CSS does
  not belong in the framework stylesheet); MMCA.Store carries the identical rules in its own CSS
  from this version's pin sweep.

## [1.116.0] - 2026-07-15

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.115.0] - 2026-07-11

### Added (2026-07-11 remediation wave 1: §18/§19 fitness gates, dark-mode a11y gate, gallery vitals)
- **`UIArchitectureConventionTestsBase`** (`MMCA.Common.Testing.Architecture`, rubric §18): file-scan
  fitness base enforcing the container/presentational split mechanically: every `*.razor.cs` under
  `Source/` stays within a 400-line cap, and every `.razor` file keeps its inline `@code` block within
  120 lines (substantial logic belongs in the code-behind partial). Seams: `MaxCodeBehindLines`,
  `MaxInlineCodeLines`, `MinimumCodeBehindFiles` (non-vacuity guard), `ExcludedPathFragments`.
  Subclassed in-repo as `UIArchitectureConventionTests`.
- **`StateManagementConventionTestsBase`** (`MMCA.Common.Testing.Architecture`, rubric §19): fitness
  base failing the build on any mutable static field or settable static property in a repo's
  `Layer.Ui` assemblies (the Blazor Server cross-circuit state-leak shape; compiler-generated members
  excluded, deliberate exceptions recorded via `AllowedStaticMembers`), plus a source scan forbidding
  singleton registration of stateful UI services (`*StateService`/`*StateContainer`). Subclassed
  in-repo as `StateManagementConventionTests` (one recorded exception: the `ErrorMessages._localizer`
  write-once wiring seam).
- **Dark-mode axe gate** (`DarkModeE2ETests`, rubric §20/§21): the gallery Login + Components pages are
  re-scanned with the dark palette active (seeded via the `mmca_theme` cookie) inside the blocking
  chromium `ui-e2e` job, closing the tracked dark-palette contrast item.
- **Gallery Core Web Vitals budgets** (`WebVitalsE2ETests`, rubric §23): LCP/TTFB/CLS measured on the
  gallery Login + Components pages with the shipped `WebVitalsCollector` and asserted against budgets
  in the blocking chromium `ui-e2e` job, so shared-chrome front-end performance is measured and
  enforced, not assumed.

### Changed (2026-07-11 remediation wave 1)
- **Dark palette WCAG AA contrast fix** (`MMCATheme.PaletteDark`): `PrimaryContrastText` and
  `ErrorContrastText` are now dark (`rgba(0,0,0,0.87)`), the Material dark-theme treatment for the
  lightened `Primary` (#42A5F5) and `Error` (#EF5350) shades. The filled-primary button label
  (was ~2.65:1) and the filled error-alert message text (was ~3.5:1) now pass the 4.5:1 floor. Filled
  primary/error surfaces render dark-on-color instead of white-on-color in dark mode.
- **`MobileInfiniteScrollList` and `NotificationBell` split to code-behind partials** (§18 conformance;
  markup and behavior unchanged, render-snapshot and bUnit suites green). `NotificationBell`'s event
  handlers dropped `async void` for explicit fire-and-forget discards and its `Dispose` adopted the
  standard `Dispose(bool)` pattern.

## [1.114.0] - 2026-07-11

### Added (2026-07-11 move-to-Common extraction wave, E1-E12 of `Docs/Planning/DriftAnalysis-plan.md` 2026-07-11)
- **`RouteAuthorizationTestsBase`** (`MMCA.Common.Testing.Architecture`): reflection fitness base
  asserting every governed routable Blazor page carries the required role, with seams
  `TargetAssembly` / `RequiredRole` / `IsGovernedPage(Type)` / `MinimumGovernedPages` and a
  non-vacuity guard; replaces five hand-rolled per-repo copies. Attribute detection is
  full-name-based, preserving the package's zero-ASP.NET-reference design.
- **Contract-test bases** (`MMCA.Common.Testing`): `ServiceInfoVersioningContractTestsBase<T>`
  (the whole /ServiceInfo v1/v2 + version-headers body), `OpenApiContractTestsBase<T>`
  (document served + well-formed 3.x, seams `MinimumPathCount` / `CorePublicResources`), and
  `ProblemDetailsContractTestsBase<T>` (RFC 9457 shape probes + the shared
  `AssertProblemDetailsShapeAsync` helper); app-specific 409 facts stay app-side.
- **UI HTTP-service test harness** (`MMCA.Common.Testing.UI`): `CapturingHttpMessageHandler`
  (responder-delegate AND route-registration modes, 404 default), `CapturedRequest`,
  `UiHttpServiceHarness` + `FreshApiClientFactory` (fresh "APIClient" per call),
  `StubTokenStorageService` (swappable `AccessTokenProvider`), and static `HttpTestDoubles`
  helpers; replaces four hand-rolled per-repo doubles sets.
- **`ClickAndWaitForUrlAsync`** (`MMCA.Common.Testing.E2E` `PageExtensions`): URL-navigation twin
  of `ClickAndVerifyAsync` (click + URL wait + reclick belt), lifted from Store's
  row-navigation helper.
- **`ImageContentSniffer`** (`MMCA.Common.Application`): dependency-free magic-byte jpeg/png/webp
  allowlist beside `IImageProcessor` (ADR-045); app-side size limits and error codes stay
  app-side.
- **`MapAppAssociationEndpoints` + `AppAssociationOptions`** (`MMCA.Common.API`): config-driven
  `/.well-known/assetlinks.json` and `/apple-app-site-association` mappers (ADR-043) beside the
  JWKS/OIDC mappers; the per-app applinks component list is passed via options.
- **`WithE2eRsaKeys`** (`MMCA.Common.Aspire.Hosting`): opt-in AppHost extension forwarding the
  E2E ephemeral RS256 keypair env vars onto the Identity resource, replacing the identical
  inline blocks in both consumer AppHosts.
- **`IFormFactor` concretes**: `WebFormFactor` (`MMCA.Common.UI.Web`, + `AddCommonWebFormFactor()`),
  `WasmFormFactor` (`MMCA.Common.UI`, + `AddWasmFormFactor()`), `MauiFormFactor`
  (`MMCA.Common.UI.Maui`, + `AddMauiFormFactor()`); replaces six per-host copies.
- **`BiometricGate`** (`MMCA.Common.UI` `Components/Capabilities`): the app-lock overlay component
  with its en/es resx pair (de-branded strings), plus `DevicePreferenceKeys.AppLockEnabled`
  (`"applock.enabled"`; consumers migrating from an app-prefixed key need a one-time app-side
  preference migration).
- **`MmcaThemeProviders`** (`MMCA.Common.UI`): the four Mud providers + the ADR-028 Day/Dark
  lifecycle in one component; `MainLayout` now renders it instead of carrying the inline block.

## [1.113.0] - 2026-07-11

### Added (2026-07-11 managed file storage + avatars, [ADR-045](https://ivanball.github.io/docs/adr/045-managed-file-storage-and-avatars.html))
- **`IFileStorageService`** with unconfigured Null default and `AzureBlobFileStorageService`
  swapped in by `AddAzureBlobFileStorage(configuration)` when the `FileStorage` section is
  complete (`ContainerName` + `ServiceUri` for DefaultAzureCredential auth or
  `ConnectionString` for local Azurite). New pins: `Azure.Storage.Blobs`, `Azure.Identity`.
- **`IImageProcessor`** with `ImageSharpImageProcessor` (always registered): decodes untrusted
  uploads, bakes in EXIF orientation, center-crops to an exact square, strips ALL metadata
  (EXIF GPS is PII), and re-encodes as JPEG so only pixels survive; undecodable content is a
  validation failure. New pin: `SixLabors.ImageSharp` (Six Labors Split License; Apache 2.0
  terms apply to this project's use).
- **`IMediaPickerService` UI capability** (ADR-042 pattern) with `MauiMediaPickerService` in
  UI.Maui (photo pick/capture, permission flow encapsulated, cancelled/denied returns null);
  web heads keep the Null default and render an `InputFile` instead.

### Added (2026-07-11 native push delivery, [ADR-044](https://ivanball.github.io/docs/adr/044-native-push-delivery.html))
- **Native push pipeline (third notification channel)**: `INativePushSender` +
  `IPushDeviceRegistrar` Application abstractions with inert Null defaults, Azure Notification
  Hubs implementations (installation model, `user:{id}` tags, FCM v1 + APNs payloads, tag
  expressions OR-chunked at the hub's 20-tag cap) swapped in by
  `AddNativePushNotifications(configuration)` only when the `NativePush` section is enabled and
  complete. `SendPushNotificationHandler` gains the OS-level leg after the SignalR attempt,
  best-effort and non-fatal (new constructor parameter; DI-resolved, so hosts are unaffected).
  New `DevicesController` (PUT/DELETE `/Notifications/Devices`, any authenticated user,
  feature-gated with `Notification.PushNotifications`) ships through the existing
  `AddNotificationControllers` application part. New pin: `Microsoft.Azure.NotificationHubs`.
- **Client-side push registration capability** (ADR-042 pattern): `IPushRegistrationService` +
  `IPushDeviceTokenProvider` contracts with inert defaults in `MMCA.Common.UI`;
  `MauiPushRegistrationService` in UI.Maui (stable installation id in device preferences,
  registration synced over the API client); `PushRegistrationListener` component re-registers
  on auth-state changes; `AuthUIService.LogoutAsync` unregisters the device BEFORE clearing
  tokens (`AuthUIService` gains a constructor parameter; DI-resolved). Everything stays inert
  until the app registers a credentialed token provider (Firebase / APNs).

## [1.112.1] - 2026-07-10

### Fixed (2026-07-10 v1.112.1 OAuth allowlist null-section regression)
- **`OAuthControllerBase` no longer throws when `IConfiguration.GetSection` returns null**
  (loose configuration test doubles in consumer suites; surfaced by ADC's
  `OAuthControllerTests` failing the v1.112.0 sweep CI). A null/missing
  `OAuth:AllowedReturnUrlSchemes` section now means "empty allowlist" (the exact pre-ADR-043
  behavior); pinned by a Common-side regression test using a mocked configuration.

## [1.112.0] - 2026-07-10

### Added (2026-07-10 device-capability layer, [ADR-042](https://ivanball.github.io/docs/adr/042-device-capability-abstraction.html) / [ADR-043](https://ivanball.github.io/docs/adr/043-mobile-deep-links-and-native-oauth-callback.html))
- **`MMCA.Common.UI.Maui` (NEW, fifteenth package)**: native device-capability implementations for
  MAUI Blazor Hybrid heads over MAUI Essentials + Plugin.LocalNotification (connectivity, battery,
  share sheet, clipboard, haptics/vibration, maps launch, geolocation, system-browser links,
  text-to-speech, screen-reader announce, local notifications with tap-to-deep-link, screenshot,
  device preferences, offline JSON cache). Register with `builder.UseMauiDeviceCapabilities()`
  AFTER `AddUIShared`. The package multi-targets the four MAUI TFMs, lives outside
  `MMCA.Common.slnx`, and is built/packed by dedicated windows CI jobs (`build-maui`,
  `publish-maui`); its layer boundary (UI + Shared only) is compile-time enforced.
- **Device-capability contracts + safe defaults in `MMCA.Common.UI`**
  (`Services/Capabilities`, ADR-042): 18 per-capability interfaces with null/neutral fallbacks
  TryAdd-registered by `AddUIShared`, plus `AddBrowserDeviceCapabilities()` overrides for web heads
  (`navigator.share`/clipboard/onLine watching, aria-live announcements, localStorage preferences
  and cache via the new `capabilities-interop.js`; all prerender-safe). New shared components:
  `DeepLinkListener` (native route requests -> Blazor navigation, cold-start buffered),
  `ExternalLink` (replaces raw `target="_blank"`, which dead-ends inside a BlazorWebView), and
  `OfflineBanner` (localized en-US + es).
- **Native OAuth callback allowlist** (`OAuth:AllowedReturnUrlSchemes`, ADR-043):
  `OAuthControllerBase.CompleteAsync` can redirect the single-use completion code (and completion
  errors) to an allow-listed custom scheme (e.g. `atldevcon://oauth-complete`) so a MAUI head's
  `WebAuthenticator` flow can capture it. http(s) never matches (no open redirect); the default
  empty list preserves the previous behavior exactly.
- **`IcsCalendarBuilder` + `IcsEvent`** (MMCA.Common.Shared, `Calendars/`): dependency-free
  RFC 5545 writer (UTC-only timestamps, TEXT escaping, CRLF + 75-octet folding that never splits a
  multi-byte character, deterministic via caller-supplied DTSTAMP) for the upcoming
  add-to-calendar endpoints.

## [1.111.0] - 2026-07-10

### Fixed (2026-07-10 output-cache policy regressions, [ADR-040](https://ivanball.github.io/docs/adr/040-authenticated-output-caching-for-public-reads.html))
- **`PublicEndpointOutputCachePolicy` now varies the cache key by every query-string parameter**
  (`CacheVaryByRules.QueryKeys = "*"`, the same rule as the built-in default policy). The v1.110.0
  policy replaced the whole default-policy chain, so it silently dropped query variance: every
  search, paging, filter, and field-projection variant of a path shared ONE cache entry, serving
  whichever response populated first (surfaced as ADC/Store integration + E2E gate failures on
  the v1.110.0 sweep deploys, e.g. a no-ids `variant-lookup` returning another test's cached
  non-empty payload and grid reads returning wrong pages).

### Added (2026-07-10 output-cache bypass roles)
- **`AddPublicEndpointPolicy(name, expiration, bypassRoles, tags)` overload** (and the matching
  `PublicEndpointOutputCachePolicy(expiration, bypassRoles, tags)` constructor): callers in a
  bypass role skip the output cache entirely (no lookup, no storage) and always read fresh. Use
  for `[AllowAnonymous]` endpoints whose payload is identical for every caller EXCEPT a privileged
  role receiving an elevated payload (e.g. ADC organizers see unpublished events per BR-108).
  Without this, an elevated response could be cached and served verbatim to anonymous callers.

## [1.110.0] - 2026-07-10

### Changed (2026-07-10 notification inbox live refresh)
- **`NotificationInbox` reloads on real-time push**: the inbox page now subscribes to
  `NotificationState.OnRefreshRequested` (the same signal `NotificationListener` raises on every
  SignalR `ReceiveNotification`) and reloads its current page, so an open inbox shows a new
  notification without navigation. Previously a push only produced a toast and a bell-badge bump.
  Overlapping refreshes coalesce (a push arriving mid-load is skipped; the next push or
  navigation reconciles).

### Added (2026-07-10 runtime performance wave, [ADR-040](https://ivanball.github.io/docs/adr/040-authenticated-output-caching-for-public-reads.html))
- **`PublicEndpointOutputCachePolicy` + `OutputCacheOptions.AddPublicEndpointPolicy(name, expiration, tags)`**
  (MMCA.Common.API): output-cache policy for `[AllowAnonymous]`, user-independent GET endpoints
  that caches despite an `Authorization` header. The UI attaches a Bearer token to every request,
  so the built-in default policy served logged-in users a 0% cache hit rate and every public read
  landed on the database; see ADR-040 for the strict apply-only-to-identity-independent contract.
- **`HttpResilienceDefaults`** (MMCA.Common.Shared.Resilience): single source of truth for the
  outbound-HTTP resilience and socket-handler values shared by `MMCA.Common.Aspire` and
  `MMCA.Common.Grpc` (the two hand-mirrored copies had drifted). MMCA.Common.Aspire now
  references MMCA.Common.Shared.

### Changed (2026-07-10 runtime performance wave)
- **BREAKING: `IEntityQueryService` shaped payloads widened from `ExpandoObject` to `object`**
  (`GetAllAsync` returns `Result<PagedCollectionResult<object>>`, `GetByIdAsync` returns
  `Result<object>`). When no `fields` subset is requested (the overwhelming majority of list/read
  traffic), the typed DTOs are now returned as-is instead of being reshaped into one
  `ExpandoObject` per row (per-row allocation + boxing + slower dictionary serialization on 100%
  of list GETs). Explicit `fields` selections still shape. The wire format is unchanged (same
  camelCase JSON); consumers only need mechanical retyping where they name the old generic
  (typically controller-test mocks). Note: DTO `[JsonPropertyName]`/`[JsonIgnore]` attributes are
  now honored on unshaped responses (the Expando path ignored them); no shipped DTO relied on that.
- **Outbox mark-processed is set-based and fully async**: after in-process dispatch,
  `DomainEventSaveChangesInterceptor` (and `InProcessEventBus`) stamp `ProcessedOn` with one
  `ExecuteUpdateAsync` instead of a nested synchronous `SaveChanges()`. This removes a blocking
  thread-pool DB round trip (plus a full re-entrant interceptor pipeline) from every
  event-raising command in every consumer. `InProcessEventBus` batch publishes now persist all
  events in ONE save and dispatch in one call (previously 2 round trips per event); a dispatch
  failure leaves the whole batch for the outbox processor (at-least-once, inbox dedup unchanged).
- **`Result` success path is allocation-free**: the error list is lazily allocated (every Result
  previously allocated a `List<Error>` even on success) and `Result.Success()` returns a shared
  instance. **`Result`/`Result<T>` are now JSON round-trippable** via an attribute-applied
  converter factory (`{"value": ..., "errors": [...]}` shape): required by the distributed query
  cache, where a Redis hit previously could not rehydrate (internal ctors, get-only `Value`) and
  the in-memory fallback masked it.
- **`CachingQueryDecorator` gained stampede protection**: per-cache-key double-check locking (the
  `IdempotencyFilter` pattern) so concurrent misses on a hot expired key run the handler once.
- **`DistributedCacheService`**: prefix invalidation now deletes keys in batches of 512 (one
  Redis round trip per batch instead of one per key, sequential, on the request thread);
  serialization uses `SerializeToUtf8Bytes` (drops a full buffer copy per cache write).
- **Retry ownership bounded**: the standard resilience handler applied to every factory client by
  `AddServiceDefaults()` now retries ONCE (was 3): the UI service base classes own user-facing
  retries, and stacked budgets amplified a backend brownout up to 16x. `AddTypedGrpcClient`'s
  resilience options now mirror the Aspire defaults exactly (they had silently drifted to the
  10s/30s library defaults) and its `SocketsHttpHandler` regains `PooledConnectionLifetime` +
  keep-alive pings (gRPC connections were never recycled, pinning stale ACA replicas after scale
  events).
- **`EFReadRepository.ApplyIncludes` opts into split query when any string include targets a
  collection navigation** (cached reflection walk), mirroring the query pipeline's heuristic:
  sibling collection includes on the direct repository path multiplied rows via JOIN products.
- **`LoggingCommandDecorator`**: the started line dropped to Debug (the completion line already
  carries name + duration; two Information rows per command doubled ingestion) and the logging
  scope is source-generated (`LoggerMessage.DefineScope`) instead of a per-command dictionary.
- **Reflection off hot paths**: `DomainEventDispatcher` caches the closed handler type beside the
  compiled invoker (no `MakeGenericType` per dispatch); `ResultFailureFactory` compiles the
  generic failure constructor once per closed type (no `MethodInfo.Invoke` per short-circuit);
  `OutboxMessage.DeserializeEvent` caches `Type.GetType` lookups and now deserializes with the
  same cycle-ignoring options used to serialize; `EFRepository.UpdateAsync` uses the tracker's
  O(1) `LocalView.FindEntry` instead of scanning the local view.
- **Gzip response compression level moved from `SmallestSize` to `Fastest`** (Brotli already
  `Fastest`): dynamic API payloads on fractional vCPUs.

## [1.109.0] - 2026-07-10

### Changed (2026-07-09 domain rejection messages in error toasts, [ADR-027](https://ivanball.github.io/docs/adr/027-multi-locale-i18n.html) Decision 9 carve-out)
- **`ErrorMessages.LoadError/SaveError/DeleteError` surface a `DomainInvariantViolationException`
  message verbatim** in place of the generic "Error loading/saving/deleting {0}." template, and the
  new **`ErrorMessages.ActionError(ex, localizedFallback)`** does the same for pages whose fallback
  is a whole-sentence snackbar key of their own resource pair. `ServiceExceptionHelper` mints that
  exception type exclusively from the API's Problem Details errors, whose text is curated domain
  wording already localized server-side to the request culture (Accept-Language via
  `CultureDelegatingHandler`), so users now see the actual business rule that rejected an action
  ("This action is only available while the event is live.") instead of a generic failure toast.
  Behavior change, not a breaking one: no signatures moved, and every other exception type still
  gets the generic localized message (raw exception text is still never shown, ADR-027 Decision 9).

## [1.108.0] - 2026-07-09

### Added (2026-07-09 live channels, [ADR-039](https://ivanball.github.io/docs/adr/039-live-channel-push.html))
- **Ephemeral live channel events over the existing notification hub**: `NotificationHub` gains its
  first client-invokable methods, `JoinChannel` / `LeaveChannel` (SignalR group membership; channel
  keys validated against the new `PushNotificationSettings.ChannelKeyPattern`, default
  `^(event|session):[0-9]+$`, invalid keys rejected with `HubException`), plus a
  `ReceiveChannelEvent` client method. A new `ILiveChannelPublisher` abstraction (Application,
  beside `IPushNotificationSender`) publishes `(channelKey, eventName, payloadJson)` to a channel;
  `SignalRLiveChannelPublisher` delivers via group send, and the no-op `NullLiveChannelPublisher`
  is the default registration, swapped by `AddPushNotifications()` (the ADR-024 pattern).
  `NotificationHubService` (UI) gains `JoinChannelAsync` / `LeaveChannelAsync` and multicast
  `OnChannelEvent` subscriptions on the existing connection, and re-joins tracked channels
  automatically after an automatic reconnect (SignalR group membership does not survive one).
  Fully additive: the `IPushNotificationSettings` interface and all existing notification
  behavior are unchanged.

## [1.107.0] - 2026-07-07

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.106.0] - 2026-07-05

### Fixed (2026-07-05 defect-fix wave C-1..C-5)
- **`LoginProtectionService` lockout backoff no longer overflows on deep failure counts** (C-1,
  security): the shift exponent is clamped to 30, so 31 or more excess failed attempts keep the
  `MaxLockoutSeconds` cap instead of computing a negative or wrapped-back-to-seconds lockout TTL
  (C# masks int shift counts to 5 bits).
- **`OAuthControllerBase.CompleteAsync` no longer throws when the ticket has no returnUrl** (C-2):
  an external-login ticket whose `AuthenticationProperties` omits the `returnUrl` item now completes
  with the `/` fallback instead of failing the whole OAuth flow with `KeyNotFoundException`.
- **Query metrics no longer count business failures as completed** (C-3): `LoggingQueryDecorator`
  inspects the returned `Result` the same way the command decorator does, so `cqrs.query.duration`
  records `outcome=failed` for `Result.IsFailure` (and logs a warning with the error summary)
  instead of conflating failures with successes.
- **`ChildEntityServiceBase` now attaches the JWT Bearer token to its requests** (C-4,
  **consumer-breaking**): it derives from `AuthenticatedServiceBase` and its constructor now
  requires an `ITokenStorageService` between the `IHttpClientFactory` and the endpoint, which
  subclasses must pass through. Previously every join-entity POST/DELETE was sent anonymously and
  failed against `[Authorize]` endpoints; consumer subclasses must add the parameter in the same
  release sweep.
- **`EntityServiceBase.GetAllForLookupAsync` escapes `nameProperty`** (C-5): a space, ampersand, or
  other reserved character in the lookup property name is now percent-encoded (the same treatment
  the paged path gives its sort/filter parameters) instead of corrupting the query string.

### Changed (2026-07-05 TimeProvider seams C-6/C-7)
- **`OutboxCleanupService` gains an optional trailing `TimeProvider` constructor parameter** (C-6,
  non-breaking, defaults to `TimeProvider.System`): the hour-scale sweep interval and the retention
  cutoff run on the injectable clock, making the purge sweep deterministically unit-testable with
  `FakeTimeProvider` (which the new sweep tests do).
- **`SessionCookieAuthenticationHandler` checks JWT expiry against the handler's `TimeProvider`**
  (C-7) instead of `DateTime.UtcNow`; no constructor change (set `options.TimeProvider` in tests
  for a deterministic clock).

## [1.105.2] - 2026-07-04

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.105.1] - 2026-07-04

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.105.0] - 2026-07-04

### Added (2026-07-04 user-preferences E2E base, §14/§27/§28)
- **`UserPreferencesTestsBase`** (`MMCA.Common.Testing.E2E`, `Workflows.Preferences`): three
  self-contained facts consumers inherit with a one-line subclass: Spanish culture switch with
  cookie persistence across reload (probed via the shared /login "Welcome Back" heading), dark-mode
  toggle asserting the emitted `--mud-palette-background` variable flips to the PaletteDark value
  and persists across reload, and a 390px-viewport fact pinning the v1.103.0 mobile top-row
  controls in real apps (not just the gallery). No app-specific overrides needed.

## [1.104.2] - 2026-07-04

### Fixed (2026-07-04 logout-then-login race, remaining site)
- **`ProfileManagementTestsBase.ChangePassword_WithValidCurrentPassword_ShouldSucceed` is now
  navigation-safe**: it waited on `LoadState.Load` after the sign-out click (already fired for the
  current document), so the re-login raced the in-flight logout forceLoad and died with
  `net::ERR_ABORTED` / "interrupted by another navigation" on contended runners (deterministic on
  Store's v1.104.1 e2e-gate). Now waits for the `/login` URL, the same fix v1.103.1 applied to
  `UserLoginTestsBase`; this was the one remaining sign-out-then-login site on the racy pattern.

## [1.104.1] - 2026-07-04

### Fixed (2026-07-04 warning-chip contrast, §20/§22)
- **Filled Warning components now meet WCAG 2.1 AA in both palettes** (`MMCATheme`): MudBlazor's
  default white contrast text is ~2.65:1 on the light palette's `#F57F17` (and ~2.0:1 on the dark
  palette's `#FFA726`); `WarningContrastText` is now dark in both palettes (~7.9:1 / ~10.8:1, the
  standard Material treatment on amber). Latent until Store's new Buy Now E2E put a "Pending
  Payment" chip on the gated admin-order-list axe scan. Visual change: warning chips/buttons
  render dark-on-amber instead of white-on-amber.

## [1.104.0] - 2026-07-04

### Added (2026-07-04 E2E authorization depth, §14)
- **`AuthorizationTestsBase.AdminPaths` + `RegisteredUser_AdminPages_ShouldBeForbidden`**
  (`MMCA.Common.Testing.E2E`): consumers declare their admin-only routes and the shared base verifies a
  freshly-registered regular user gets the shared Forbidden page ("Access Denied") on each: the
  escalation direction the anonymous-redirect test cannot cover. Empty default keeps apps without an
  admin surface passing unchanged.

### Changed (2026-07-04 E2E authorization depth, §14)
- **`ProfileManagementTestsBase.ChangeEmail_ShouldUpdateEmail` no longer probes the DOM** to decide
  whether email change exists (that made it pass vacuously on apps whose profile page has no email
  section). It is now gated by the new `ProfileSupportsEmailChange` opt-in (default false); a consumer
  that opts in gets a loud failure when the email field goes missing. No consumer opts in today, so
  observed behavior is unchanged: the test's silence is now declared instead of accidental.

## [1.103.1] - 2026-07-04

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.103.0] - 2026-07-04

### Fixed (2026-07-04 mobile parity)
- **Culture + theme controls are now reachable on phones (§22 / ADR-027/028).** The shared layout
  hides the whole `MudAppBar` below 1024px, and the `CultureSwitcher`/`ThemeToggle` lived only there,
  so no phone user (anonymous or signed-in) could switch language or theme. `NavMenu`'s mobile
  top-row now renders both controls unconditionally (module app-bar components and the user name
  stay auth-gated); existing top-row CSS handles compact sizing, white icons, and the desktop hide,
  so nothing renders twice. Pinned by `MobileTopRowE2ETests` (phone + desktop viewports) in the
  required chromium `ui-e2e` job.

## [1.102.0] - 2026-07-04

i18n completion train (ADR-027 amended 2026-07-03, §27): every remaining user-visible literal in the
shared UI is externalized, MudBlazor chrome localizes, and two new gates keep it that way.
**Consumer-breaking on purpose:** `ErrorMessages.Success(entity, action)` is now `[Obsolete]`
(a warning, which consumer `TreatWarningsAsErrors` promotes to a build error), forcing the same-pass
sweep to whole-sentence page resource keys per ADR-016's lockstep rule.

### Added (2026-07-03 i18n completion)
- **`ResxMudLocalizer` + `MudTranslations.{resx,es.resx}`** (`MMCA.Common.UI`): MudBlazor built-in
  component text (data-grid pager and filter menus, pickers, table editing, pagination, close buttons)
  now follows the active culture; all built-in keys of the pinned MudBlazor version ship en + es.
  Registered in `AddUIShared` via `TryAddTransient<MudLocalizer, ResxMudLocalizer>` (guarded by a DI
  resolution test).
- **`LocalizedTextConventionTestsBase` + `ArchitectureRules.UserVisibleTextIsLocalized`**
  (`MMCA.Common.Testing.Architecture`, now 78 methods / 25 bases): fails the build on hard-coded
  snackbar literals, literal page `Title` properties, literal `<PageTitle>` markup, literal breadcrumb
  labels, and `NavItem` rows without a `TitleResource`; per-line `i18n: allow` marker for deliberate
  literals (brand names). Subclass in every repo.
- **Pseudo-localization CI gate** (`PseudoLocalizationE2ETests`, required chromium `ui-e2e` job): the
  gallery renders `/login`, `/register`, `/components` under `qps-Ploc` and asserts the bracket
  sentinel appears and no horizontal overflow occurs under the ~40% expansion (rubric §27 layout
  tolerance); an `en-US` leak-guard asserts the sentinel never ships to a real locale.
- **`NavItem.TitleResource`** (optional, defaulted): when set, the shared `NavMenu` resolves
  `Title`/`Group` as resource keys per circuit so module nav menus follow the culture; literal-titled
  items render unchanged.
- **Fully localized shared chrome** (`SharedResource.{resx,es.resx}`, 136 keys): NavMenu, Login,
  Register, OAuthComplete, Forbidden, NotFound, Home fallback, Counter, the notification pages
  (titles, breadcrumbs, table headers, status chips, form labels), ReconnectModal, EmptyState,
  PageErrorState, PageLoadingState, DeleteConfirmation, UnsavedChangesGuard, MobileCardList,
  MobileInfiniteScrollList, and the `UI.Web` SSR Error page.

### Changed (2026-07-03 i18n completion)
- **`Common.Error.Load/Save/Delete` resource values no longer append raw `ex.Message`** (neither
  localizable nor safe to surface); method signatures are unchanged, extra format args are ignored.
- **Component parameter defaults localize** (`UnsavedChangesGuard`, `PageLoadingState`, `EmptyState`,
  `PageErrorState`, `DeleteConfirmation`, `MobileCardList`, `MobileInfiniteScrollList`): the affected
  string parameters are now nullable with localized fallbacks; explicit consumer values still win.
- **The shared Register page subtitle is generic** ("Create your account to get started"): the
  previous literal was ADC-conference copy leaking into every consumer of the shared page.
- **`LocalizationResourceTests` (Common) sets `MinimumBaseResources = 3`** so the completeness gate
  can no longer pass vacuously.

### Deprecated (2026-07-03 i18n completion)
- **`ErrorMessages.Success(entity, action)`**: composed sentences cannot be translated (Spanish gender
  agreement breaks). Use a whole-sentence key in the page's own resource pair, e.g.
  `Snackbar.Add(L["Snackbar.Created"], ...)`.

## [1.101.0] - 2026-07-03

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.100.0] - 2026-07-02

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.99.0] - 2026-07-02

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.98.0] - 2026-07-01

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.97.0] - 2026-07-01

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.96.0] - 2026-07-01

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.95.0] - 2026-07-01

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.94.0] - 2026-06-30

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.93.0] - 2026-06-30

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.92.0] - 2026-06-29

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.91.0] - 2026-06-29

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.90.0] - 2026-06-28

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.89.0] - 2026-06-28

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.88.0] - 2026-06-28

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.87.0] - 2026-06-28

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.86.0] - 2026-06-27

Internationalization (ADR-027) + Day/Dark theme mode (ADR-028), plus maturity-axis remediation (§29, §30)
and DDD fitness hardening (§4). No breaking changes (the static `ErrorMessages` signatures are preserved).

### Added
- **Multi-locale i18n (ADR-027, supersedes ADR-011).** Framework now supports `en-US` + Spanish (`es`):
  - **Edge error localization keyed by `Error.Code`.** `IErrorLocalizer` (`MMCA.Common.API/Localization`)
    translates the human-readable message at the HTTP edge (`ErrorHttpMapping.BuildErrorsExtension`,
    applied in `ApiControllerBase.HandleFailure` + `UnhandledResultFailureFilter`), falling back to the
    English `Error.Message` for any unmapped code; the ProblemDetails `title` and the `Code`/`Source`/
    `Target` stay verbatim. Common ships `ErrorResources.{resx,es.resx}`; modules add their own via
    `AddErrorResources<TResource>()`. Wired automatically by `AddAPI` (`AddErrorLocalization`).
  - **Request localization + culture switch.** `UseCommonRequestLocalization()` (in the shared service
    pipeline) and `MapCultureEndpoint()` (`GET /culture/set`) plus a `SupportedCultures` allowlist
    (`MMCA.Common.Shared`). UI: `AddUIShared` registers `AddLocalization()` + a `CultureDelegatingHandler`
    that forwards the active culture as `Accept-Language`; a `CultureSwitcher` component; and
    `MmcaCultureBootstrap.SetBrowserCultureAsync` for the WASM `.Client` to match SSR (no locale flash).
  - **Localized shared chrome.** `MainLayout`, `ErrorMessages`, and `DataGridListPageBase` snackbars now
    resolve from `SharedResource.{resx,es.resx}` via `IStringLocalizer`.
- **Day/Dark theme mode (ADR-028).** `MudThemeProvider` is now bound (`@bind-IsDarkMode`) to the existing
  `MMCATheme.PaletteDark`; a `ThemeService` persists the choice to a cookie + localStorage (default = OS
  `prefers-color-scheme`) and a `ThemeToggle` ships in the shared app bar beside the culture switcher.
- **In-repo restore-drill smoke (§29).** `DatabaseRestoreDrillTests`
  (`Tests/Core/MMCA.Common.Infrastructure.Tests/Resilience/`) exercises the full recovery procedure —
  seed → backup → simulated catastrophic data loss → restore → verify zero data loss, timing the RTO —
  against an ephemeral SQLite database via the SQLite online-backup API. The framework now demonstrates
  the restore *procedure* centrally instead of only inheriting it downstream; `RESILIENCE.md` records the
  baseline.
- **Non-vacuous PII erasure-contract fitness (§30).** `PiiErasureContractFitnessTests` forces a
  representative `[Pii]`-carrying data subject through `PiiRedactor` (masking + no clear-text leak) and
  `IAnonymizable` (idempotent in-place erasure), proving the three §30 mechanisms compose end to end —
  closing the "no fitness function forces a type through the redactor" gap (ADR-005).
- **Aggregate private-constructor fitness rule (§4).** `AggregateConventionTestsBase` now also asserts
  Domain-layer aggregate roots expose no public constructor (construction goes through the static
  `Create(...)` Result-factory) via `ArchitectureRules.DomainAggregateRootsHaveNoPublicConstructors` —
  the minimal-base counterpart to the module-scoped rule, so the framework's own aggregates are covered
  (now 71 fitness methods / 18 bases).

## [1.85.0] - 2026-06-27

Under-8 Implementation remediation: every architecture-scorecard category scored Implementation < 8
is lifted with shipped, tested evidence (reference samples + real code levers). No breaking changes.

### Added
- **Slice-cohesion fitness function (§5).** `SliceCohesionTestsBase` + `ArchitectureRules.Slices`
  in `MMCA.Common.Testing.Architecture` (now 70 methods / 18 bases) — fails the build if a
  use-case slice's handler/validator is stranded from its same-assembly command/query contract.
  Re-run as a thin subclass in every repo.
- **OTel trace sampler knob (§31).** `Telemetry:TracesSampleRatio` (a value in `(0,1)`) installs a
  `ParentBasedSampler(TraceIdRatioBasedSampler)` in `AddServiceDefaults`; unset = sample everything.
  The biggest lever on trace-ingestion cost.
- **In-shell 403 page (§25).** `Pages/Forbidden.razor` rendered for the authenticated-but-unauthorized
  route branch (was a bare alert), plus `NavigationFlow.md` documenting the Common UI route/role model.
- **Reference deployment sample (§17).** `samples/deployment/{foundation,main}.bicep` (Container Apps
  + ACR-via-managed-identity + Key Vault + SQL + cost tags + budget) + `DEPLOYMENT.md` (OIDC + UAMI
  bootstrap + smoke-gate/auto-rollback).
- **`RESILIENCE.md` (§29)** — baseline SLO/error-budget template + restore-drill runbook reference;
  the warm-up readiness subsystem is now unit-tested.
- **BenchmarkDotNet smoke harness (§12)** — `Tests/Performance/MMCA.Common.Benchmarks` (outside the
  `.slnx`); hot-path spec efficiency is now measured, not assumed.

### Changed
- **Register/Login use `EditForm` + DataAnnotations field-level validation (§24)** — errors are tied
  to the offending input (`ValidationMessage`) with the summary kept for form-level/server errors.
- **Outbox per-message "dispatched" log moved Information → Debug (§31)** — the highest-volume log
  line in steady state; failures stay loud (dead-letter = Error, retry = Warning).
- **`COST.md`** gains cost-attribution-tag + cost-guard-workflow samples and documents the sampler knob.

## [1.84.0] - 2026-06-27

PII log/telemetry redaction (§30). No breaking changes.

### Added
- **`PiiRedactor` (§30).** `Domain/Privacy/PiiRedactor.cs` masks every `[Pii]`-marked member (shallow,
  value-erasing `[REDACTED]` token, per-type reflection cache) before an entity carrying personal data
  reaches a structured log or telemetry attribute — the redaction half of the `[Pii]` contract (ADR-005),
  complementing the `IAnonymizable` erasure seam. Covered by `PiiRedactorTests` (incl. "never emits the
  clear-text PII values").

## [1.83.0] - 2026-06-26

Governance + front-end security hardening. No breaking changes.

### Added
- **ADR-023 — centralized security-response headers (§26).** Documents the hardened security-headers
  middleware + pluggable `ICspPolicyProvider` CSP seam (`AddCommonSecurityHeaders`), replacing per-host
  hand-rolled headers.
- **Source-generated, CI-gated `FACTS.md` (§34).** `build/facts` computes version / package-count /
  ADR-range / fitness counts from source; the `build-and-test` job runs it with `--check` and fails the
  build on drift, so the framework facts are a computed-and-gated artifact rather than hand-maintained prose.
- **Canonical two-axis `ArchitectureScorecard.md` (§34).** The rubric (`ArchitectureEvaluationCriteria.md`)
  and scorecard are version-controlled in-repo (mirroring the ADR governance pattern).

## [1.82.0] - 2026-06-26

Governance + supply-chain + E2E-stability hardening. No breaking changes.

### Security
- **RS256 pinned on the JWKS-forwarded auth path.** `ValidAlgorithms = [RsaSha256]` on the
  forwarded-JWT (JWKS discovery) validation path in `MMCA.Common.API` — defense-in-depth against an
  algorithm-confusion swap, matching the existing in-process pin.

### Added
- **ADRs 020-022** — 020 (permission-based authorization), 021 (consumer inbox idempotency),
  022 (browser session-cookie auth); the committed ADR set is now 001-022.

### Fixed
- **Lock drift.** Pinned the transitive `Deque.AxeCore.Commons` to 4.12.0 in
  `MMCA.Common.Testing.E2E` so a stale-cache restore no longer drifts it to 4.7.2 and dirties the lock.

### Internal
- **E2E register/login de-flake (R11).** `RegisterNewUserAsync`/`LoginAsync` now give the success
  signal a grace window (`E2ETestConfiguration.AuthGraceTimeout`, default 15s, `E2E_AUTH_GRACE`) when a
  transient error alert flashes during the success-path `forceLoad` — only a persistent error is a real
  failure. Detection-only (cannot break auth), unlike the reverted WASM-forcing.

## [1.81.0] - 2026-06-26

Post-v1.80.0 polish: an opt-in OpenAPI UI, FinOps documentation, and test-coverage hardening for the
v1.80.0 rate-limiter and `TimeProvider` seams. Additive — no breaking changes and no consumer behavior
change beyond the new opt-in helper.

### Added
- **Scalar API-reference UI helper (opt-in, §9).** `MapCommonScalarUi()` (`MMCA.Common.API`) renders the
  generated OpenAPI document as an interactive reference at `/scalar/{documentName}`, **outside Production
  only**. Opt-in (a host calls it explicitly); assets are served by the bundled `Scalar.AspNetCore` package
  (no external CDN). Pairs with `AddCommonOpenApi()` / `MapCommonOpenApi()`. Internal services behind the
  Gateway need not call it — it's for hosts run standalone where a rendered reference helps.
- **`COST.md` (FinOps notes, §31)** — consolidates the framework's cost-relevant defaults (telemetry
  poll-span filtering, outbox poll/retention tuning) and the right-sizing / attribution / surge-revert
  levers consumers set downstream.
- **ADR-019 (layered rate limiting)** documents the authenticated-only global limiter (shipped earlier),
  bringing the committed ADR set to **001-019**; the CHANGELOG was also backfilled for 1.72.0-1.80.0.

### Changed
- **`MMCA.Common.API` takes a new dependency on `Scalar.AspNetCore`** (MIT) for the optional UI helper
  above. Consumers referencing `MMCA.Common.API` pull it transitively; it has no runtime effect unless
  `MapCommonScalarUi()` is called.

### Internal
- Rate-limiter exemption/partition helpers are now `internal` (via `InternalsVisibleTo`) and unit-tested
  (`RateLimitPartitionTests` — bypass paths, anonymous-vs-authenticated, per-user partition-key fallback).
- The two notification read-handler tests now assert the stamped read-time against a fixed `TimeProvider`.
- `BaseDomainEvent.DateOccurred`'s creation-time default is documented as a deliberate occurrence-time
  choice (event-sourcing / audit semantics), not changed.

## [1.80.0] - 2026-06-25

Opt-in permission-based authorization plus `TimeProvider` adoption on the time-sensitive paths.

### Added
- **Permission-based authorization (opt-in).** `IPermissionRegistry` + `PermissionRegistryBuilder`
  (`MMCA.Common.Shared`) declare role→permission grants; `[HasPermission("x")]` (`MMCA.Common.API`)
  resolves an on-demand `perm:x` policy via `PermissionPolicyProvider`, and
  `PermissionAuthorizationHandler` grants access when the caller carries an explicit `permission`
  claim or holds a role the registry grants it. Wired through `AddAuthorizationPolicies` +
  `AddPermissions(...)`. **Backward-compatible** — the existing named role policies are untouched and
  the mechanism is inert until a host calls `AddPermissions`. It is RBAC with a role→permission
  capability indirection (policy-based, not resource/attribute-based).
- **`RoleNames.ContentEditor`** — a granular conference-content role consumers can grant a permission
  subset (used by ADC).
- **ADR-019 (layered rate limiting)** documents the always-on, authenticated-only global limiter
  (`AddCommonRateLimiting`): infrastructure traffic (`/health`, `/alive`, `/.well-known/*`,
  `application/grpc`) and anonymous requests are exempt; authenticated callers are capped per principal.
  The limiter code itself is pre-existing — this release adds the decision record. ADRs 017/018 are
  also now committed (idempotency, polyglot persistence).

### Changed
- **`TimeProvider` adoption.** `TokenService` (token `iat`/`nbf`/`exp`) and the notification read
  handlers now derive time from an injected `TimeProvider` instead of `DateTime.UtcNow`;
  `UserNotification.MarkAsRead(DateTime readOnUtc)` takes an explicit UTC timestamp, keeping the
  aggregate free of ambient clock access. Non-breaking — `TimeProvider.System` is the default.

## [1.79.0] - 2026-06-24

Polyglot-persistence ergonomics: moving an entity between data-source engines becomes a minimal,
build-guarded change (ADR-006).

### Added
- **Unified entity configuration base.** `EntityTypeConfiguration<TEntity, TIdentifierType>`
  (`MMCA.Common.Infrastructure`) declares an entity's target engine via a `[UseDataSource]` attribute
  and applies the matching table/container/key conventions. `EntityTypeConfigurationSQLServer`,
  `…Sqlite`, and `…Cosmos` are now thin attribute-carrying shims over it, so changing an entity's engine
  is a one-token base-class (or attribute) change with no configuration-body edits.
- **Cosmos / SQLite AppHost wiring.** `WithCosmosDataSource(...)` and `WithSqliteDataSource(...)`
  Aspire.Hosting extensions (alongside the SQL Server helper) for routing a module to a polyglot data
  source.
- **Cross-source specification helper.** `CrossSourceSpecification.BuildAsync(...)` plus
  `InlineSpecification` build a translatable `localPredicate AND foreignKey IN (resolved keys)` filter
  for a dependent entity whose principal lives in a different physical data source — where a navigation
  join is not translatable (e.g. a Cosmos dependent and a SQL Server principal).
- **Specification fitness rule (opt-in).** `ArchitectureRules.SpecificationsDoNotNavigateToOtherEntities`
  + `SpecificationConventionTestsBase` (`MMCA.Common.Testing.Architecture`) fail the build when a
  specification's `Criteria` navigates to another entity — a latent cross-source hazard in a
  database-per-service / polyglot setup. Polyglot-capable repos opt in; single-engine repos need not.

### Changed
- **(Breaking)** Renamed the Aspire.Hosting extension `WithDataSource` → **`WithSQLServerDataSource`**
  for `With*DataSource` naming consistency with the new Cosmos/SQLite helpers. Consumers update their
  AppHost calls to `service.WithSQLServerDataSource(db, "Module")`.

### Fixed
- **Cosmos config-body portability.** `CrossDataSourceDegradeConvention` no longer adds a compensating
  index when degrading a cross-source foreign key in a **Cosmos** context — the Cosmos provider rejects
  index definitions, so the re-added index previously failed model validation. A configuration body that
  keeps a cross-source relationship (or a filtered index) is now portable to Cosmos unchanged.
- **SQLite schema under the `"Migrate"` strategy.** `DatabaseInitializationExtensions` now
  `EnsureCreated`s SQLite sources (which have no EF migrations) up front, independent of the
  SQL-Server-oriented strategy; previously a SQLite source in use was never created under `"Migrate"`
  (or `"None"`) and the first repository call failed.
- **Cosmos container naming.** `EntityTypeConfigurationCosmos` derives the container from the module
  namespace segment preceding `Domain` (the same rule as the SQL schema / logical database name); it
  previously looked for a `Modules` segment that the actual namespaces do not contain, falling back to a
  per-type container.

## [1.78.0] - 2026-06-23

### Changed
- Upgraded NuGet dependencies to their latest stable versions (held packages — MassTransit v8,
  `Microsoft.VisualStudio.Threading.Analyzers`, `StackExchange.Redis`, `MessagePack` — excluded per
  the semver-major Dependabot ignores and the MassTransit-v8 license pin).

### Added
- **`GETTING-STARTED.md`** — a 9-phase framework-adoption guide (solution plumbing → a module vertical
  slice → Aspire host → architecture-fitness map → a worked module extraction), with MMCA.Helpdesk as
  its runnable companion.

## [1.77.0] - 2026-06-22

### Added
- **ADRs 013-016** — Result pattern (013), CQRS decorator pipeline (014), architecture fitness
  functions (015), lockstep versioning + MassTransit-v8 pin (016) — documenting patterns that
  previously lived only in code. Docs-only release.

## [1.76.0] - 2026-06-22

### Changed
- Documentation alignment with the workspace `Docs/` folder reorganization. Docs-only release.

## [1.75.0] - 2026-06-21

### Changed
- Drift-reduction housekeeping (D5/D22/D29/D30): consolidated the exception-handler tests, lifted the
  shared E2E scan helpers, documented the MassTransit-pin boundary (enforced only in MMCA.Common; ADC
  and Store inherit it transitively), and reconciled ADR-012.
- README now links the architecture scorecard + ADRs and lists all 13 packages.

## [1.74.0] - 2026-06-21

### Changed
- **Promoted shared cross-cutting infrastructure up into MMCA.Common** (drift-reduction P4) so the
  consumer apps inherit it instead of carrying parallel copies.

## [1.73.0] - 2026-06-21

### Added
- **`MMCA.Common.Testing.Architecture`** package (the **13th**) — define-once architecture fitness
  functions: an `IArchitectureMap`-parameterized NetArchTest rule library + abstract test bases, so
  MMCA.Common, Store, and ADC run the *same* rules as thin subclasses rather than parallel copies
  (ADR-015).

### Fixed
- Reverted the v1.72.0 "force WASM interactivity before auth submit" E2E change (it caused a CI
  regression); fixed a CI-only IDE0370 null-forgiving analyzer error in the new arch-test package.

## [1.72.0] - 2026-06-20

### Added
- **Resilience fault-injection test** (`ResilienceCircuitBreakerFaultInjectionTests`) that trips a Polly
  circuit breaker and asserts short-circuiting, plus an outbox/inbox dedup test (tests only).

## [1.71.0] - 2026-06-19

### Added
- **Broker retry policy.** `AddBrokerMessaging` now configures `UseMessageRetry` (exponential backoff)
  on both the RabbitMQ and Azure Service Bus transports. Tunable via new `MessageBus:RetryLimit`,
  `MessageBus:RetryMinIntervalSeconds`, and `MessageBus:RetryMaxIntervalSeconds` settings.
- **`IAnonymizable`** erasure seam (`MMCA.Common.Domain`) for reconciling soft-delete with
  data-subject erasure requests. See ADR-005.
- **`OutboxCleanupService`** background service that purges processed outbox rows. New settings
  `Outbox:RetentionDays` (default 7) and `Outbox:CleanupIntervalHours` (default 6).
- **bUnit** component-test harness for the shared Blazor UI primitives.
- **`MMCA.Common.Testing.UI`** package — shared bUnit component-test infrastructure: a unified
  MudBlazor/auth-aware test base (`BunitComponentTestBase`), a dialog/popover/snackbar provider
  harness, and interaction helpers. Consumed by downstream apps so component tests stop duplicating
  the bUnit/auth setup. This brings the published set to **twelve** packages.
- **Outbox dead-letter metric** is now exported (`AddMeter("MMCA.Common.Outbox")`).
- Supply-chain hardening: NuGet lock files, package source mapping, dependency vulnerability
  auditing, an SBOM at release, and Dependabot.
- `SECURITY.md`, `VERSIONING.md`, and a published breaking-change policy.

### Changed
- **(Behavior)** Processed outbox rows are now purged after `Outbox:RetentionDays` (default **7 days**).
  Previously they were retained indefinitely. Set `Outbox:RetentionDays = 0` to restore the old behavior.

### Fixed
- `IntegrationEventConsumer` no longer claims (in a comment/log) a retry policy that was never
  configured — a real retry policy is now applied (see Added).

### Security
- Dependency vulnerability audit is enforced in CI (`dotnet list package --vulnerable`), and
  `nuget.config` restricts each package to its expected source via `packageSourceMapping`.

## [1.70.0] - 2026-06-19

### Fixed
- **R24 §8** — paginated list reads with a collection include returned empty child collections (e.g.
  `GET /Sessions?includeChildren` came back with empty `sessionSpeakers` while by-id reads populated
  them). `EntityQueryPipeline` now forces `AsSplitQuery` when a child-collection navigation is included,
  so `Skip`/`Take` pagination no longer truncates child rows. Adds `IQueryableExecutor.AsSplitQuery`
  (EF bridge guarded by `IsEfQuery` for in-memory queries) + unit tests.

## [1.69.0] - 2026-06-19

### Added
- **Integration-event schema versioning** (R17, ADR-010). `BaseIntegrationEvent` exposes a
  `virtual int SchemaVersion` (default `1`, serialized with the payload) so cross-service consumers
  have an explicit version signal; a fitness test asserts every concrete `IIntegrationEvent` declares
  it. Non-breaking — existing events stay at version 1; breaking event changes use a new event type +
  upcaster (see ADR-010).
- **OpenAPI generation helpers** (R8). `AddCommonOpenApi()` / `MapCommonOpenApi()` (the latter
  Production-guarded) in `MMCA.Common.API`, so service hosts expose `/openapi/v1.json` consistently.
  Adds a dependency on `Microsoft.AspNetCore.OpenApi`.

### Changed
- **(Security, behavior)** The default Content-Security-Policy (`MMCA.Common.Aspire` SecurityHeaders,
  R18) is hardened from `frame-ancestors 'none'` to
  `default-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'`.
  It omits `script-src`/`style-src`, so HTML/Blazor hosts (which register their own `ICspPolicyProvider`)
  are unaffected; API/Gateway hosts using the default get the stricter baseline.

### Fixed
- Corrected stale comments implying auth tokens live in browser `localStorage` (R18) — access tokens
  are held in-memory; the refresh token is in an HttpOnly cookie.
- **CI:** the dependency-audit gate now honors `NuGetAuditSuppress` (it previously re-flagged accepted,
  unpatched advisories and reddened every run); the coverage floor gates the unit tier with generated
  code excluded; and the release SBOM step is now a hard gate.

---

<!--
Release process: tag `vMAJOR.MINOR.PATCH` on `main`; MinVer + the release workflow pack and push.
Move the relevant Unreleased entries under a new `## [x.y.z] - YYYY-MM-DD` heading at release time.
-->

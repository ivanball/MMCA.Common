# Changelog

All notable changes to the MMCA.Common packages are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow [Semantic Versioning](https://semver.org/)
and are derived from git tags by MinVer (see [VERSIONING.md](VERSIONING.md)).

## [Unreleased]

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

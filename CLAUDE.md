# CLAUDE.md

This file provides framework-specific guidance to Claude Code (claude.ai/code). Cross-repo conventions (.NET 10 with `LangVersion: preview`, `TreatWarningsAsErrors`, the five error-severity analyzers, Central Package Management, code style and naming, PR-based contribution flow, Microsoft Testing Platform usage) are defined once in the workspace `../CLAUDE.md`: do not re-derive them here.

## Project Overview

MMCA.Common is a .NET 10 NuGet package framework for building modular monolith applications using DDD, Clean Architecture, and CQRS. It publishes its packages to GitHub Packages, versioned in lockstep (authoritative package list and count: `FACTS.md`); it is not a runnable app. `MMCA.Common.UI.Maui` is the one MAUI-TFM package: it lives outside `MMCA.Common.slnx` and is built/packed by dedicated windows jobs (ADR-042). The framework also provides the extraction path for lifting modules into standalone microservices (gRPC transport, transport-agnostic message bus, cross-service JWKS auth, Aspire hosting extensions): see "Microservices Extraction Boundaries" below.

## Build & Test

```bash
dotnet build MMCA.Common.slnx -c Release
dotnet test --solution MMCA.Common.slnx -c Release                                # all tests
dotnet test --project Tests/Presentation/MMCA.Common.API.Tests -- -method "*IdempotencyFilterTests*"   # single test (MTP filter; -class works too)
dotnet test --project Tests/Architecture/MMCA.Common.Architecture.Tests           # NetArchTest layer/purity rules (fast, no DB)
dotnet test --project Tests/Presentation/MMCA.Common.UI.E2E.Tests/MMCA.Common.UI.E2E.Tests.csproj      # UI a11y + render smoke (NOT in slnx; needs `playwright install chromium` once)
dotnet run -c Release --project Tests/Performance/MMCA.Common.Benchmarks          # BenchmarkDotNet smoke; append `-- --job Dry` for a fast correctness pass
dotnet pack MMCA.Common.slnx -c Release -o ./nupkgs/
dotnet run --project build/facts -- .                                             # regenerate FACTS.md (CI gates on `--check` drift)
```

CI (`.github/workflows/ci.yml`) jobs on every push/PR to `main`:
- **build-and-test**: FACTS drift gate (`build/facts -- . --check`) -> restore -> Release build -> vuln audit -> tests under `dotnet-coverage` with `--minimum-expected-tests 1`. The vuln audit re-applies the `NuGetAuditSuppress` list from `Directory.Build.props` itself (`dotnet list --vulnerable` ignores suppressions).
- **ui-e2e**: cross-browser matrix (chromium required gate; firefox/webkit advisory). Builds the out-of-slnx `MMCA.Common.UI.Gallery` + `MMCA.Common.UI.E2E.Tests` directly by csproj path (deliberately excluded from the slnx to keep unit runs fast) and runs axe-core WCAG 2.1 AA scans + a render smoke against the backend-less gallery.
- **coverage**: merges the tiers (ReportGenerator) and enforces the floor: the unit tier must stay >= 53% line coverage.

Windows jobs build/pack `MMCA.Common.UI.Maui`. Versioning is MinVer from git tags (`fetch-depth: 0` required). `release.yml` triggers on `v*` tags: build/test/pack, CycloneDX SBOM as a hard gate, push to GitHub Packages.

Gotchas:
- CI runs on Ubuntu: file paths are case-sensitive; match casing exactly.
- Every test project must contain at least one test or MTP fails the build (exit code 8).
- NuGet lock files are committed (`--force-evaluate` to regenerate); `nuget.config` maps everything to nuget.org, so building/testing Common needs NO GitHub token.
- **MassTransit is pinned to v8 by policy** (v9 requires a commercial license); `DependencyVersionTests` fails the build if the major reaches 9. Consumers inherit the pin transitively and deliberately do not subclass `DependencyVersionTestsBase`.

## Source Layout

```
Source/
├── Build/MMCA.Common.LayerEnforcement.targets   # compile-time layer guard
├── Core/          MMCA.Common.{Shared,Domain,Application,Infrastructure}
├── Presentation/  MMCA.Common.{API,Grpc,UI}     (+ UI.Maui, outside the slnx)
└── Hosting/       MMCA.Common.{Aspire,Aspire.Hosting,Testing,Testing.E2E,Testing.UI,Testing.Architecture}

Tests/             # mirrors Source/; plus Architecture/ (NetArchTest) and the out-of-slnx
                   # Presentation/UI.Gallery + UI.E2E.Tests (CI ui-e2e job only)
```

All `Source/` projects are packable (bulk NuGet metadata via `Directory.Build.props`, versions from MinVer).

## Architecture

Strict layered dependency flow (each layer references only layers below): `API/Grpc -> Infrastructure -> Application -> Domain -> Shared`. Exceptions: `UI` and `Grpc` depend on **Shared only** (`UI` for Blazor WASM compatibility, `Grpc` because it is pure transport).

### Architecture Enforcement (two gates)

1. **Compile-time**: `Source/Build/MMCA.Common.LayerEnforcement.targets` (imported for every `Source/` project) fails the build on a forbidden `ProjectReference`.
2. **Runtime**: `Tests/Architecture/MMCA.Common.Architecture.Tests` (NetArchTest) asserts the same rules against compiled assemblies. The rule bodies live once in the `MMCA.Common.Testing.Architecture` package (`ArchitectureRules` + abstract `*TestsBase` parameterized by `IArchitectureMap`); Store and ADC subclass the same bases with their own maps, so rules stay identical across repos.

When moving a type between packages or changing project references, expect both gates to react; add new layer rules in **both** places.

### DI Registration Sequence

Downstream apps register `AddApplicationDecorators()` **last**: Scrutor `TryDecorate` can only wrap handlers already registered, so every module's handler scan must run first. Only that ordering is load-bearing. Reference hosts use `ModuleLoader.DiscoverAndRegister`; the fluent equivalent is `AddApplication() -> AddInfrastructure(config) -> AddAPI(modulesSettings) -> ScanModuleApplicationServices<TModuleRef>() per module -> AddApplicationDecorators()`.

### CQRS Decorator Pipeline

`ICommandHandler<TCmd, TResult>` / `IQueryHandler<TQuery, TResult>` with decorators registered in `AddApplicationDecorators()` and applied by Scrutor `TryDecorate` in reverse registration order (last registered = outermost). Execution order, outermost to innermost (ADR-014):

```
Commands: FeatureGate -> Logging -> Caching -> Validating -> Transactional -> Handler
Queries:  FeatureGate -> Logging -> Caching -> Handler
```

- **FeatureGate**: short-circuits when the command/query's feature flag is off.
- **Logging**: full pipeline duration via `ICorrelationContext`.
- **Caching**: `ICacheInvalidating` commands invalidate on success (outside the transaction); `IQueryCacheable` queries (with `CacheKey` + `CacheDuration`) cache results.
- **Validating**: FluentValidation before the transaction opens; queries have no Validating or Transactional decorator.
- **Transactional**: `ITransactional` commands get a DB transaction; exceptions roll back, business failures (`Result.Failure`) commit but skip cache invalidation.

An optional `Profiling` decorator pair is registered by a separate opt-in `AddApplicationProfiling()` call and is not wired by any host today.

### Module System

`IModule` implementations (with `Name`, `Dependencies`, `RequiresDependencies`, `Register()`, `SeedAsync()`, disabled-stub registration) are discovered via reflection and registered in topological order (Kahn's algorithm) by `ModuleLoader`. `ModulesSettings` (config section `Modules`) can disable modules; disabled modules receive stub registrations so cross-module interfaces stay resolvable. `ScanModuleApplicationServices<TAssemblyMarker>()` auto-registers domain event handlers (singleton), DTO/request mappers (scoped), command/query handlers (scoped), and validators. DI registration methods use C# preview extension types (`extension(IServiceCollection services)` blocks in `DependencyInjection.cs`).

### Entity Model

`BaseEntity<TId>` (required init `Id`) -> `AuditableBaseEntity<TId>` (adds `CreatedOn/By`, `LastModifiedOn/By`, `IsDeleted`) -> `AuditableAggregateRootEntity<TId>` (adds domain events, `GetChildOrNotFound<T>()`, `SetItems<T>()`). Aggregates use static `Create(...)` factories returning `Result<T>`; invariants live in static classes composed with `Result.Combine()`. Domain events are collected via `AddDomainEvent()` and dispatched by `DomainEventDispatcher` after `SaveChangesAsync()` within the same transaction (compiled expression delegates, cached per event type; handlers auto-discovered via Scrutor).

Identifier aliases: `GlobalUsings.IdentifierType.cs` (Domain) and `GlobalUsings.NotificationIdentifierType.cs` (Shared) are linked into all projects via `Directory.Build.props`; to add a solution-wide alias, create the `GlobalUsings.*.cs` file and a matching `<Compile Include ... Link=... />` block there.

### Multi-Database Strategy (database per microservice)

Every entity resolves to a physical data source: a `DataSourceKey(Engine, Name)` pair. Engine comes from the configuration base class (`EntityTypeConfigurationSQLServer/Cosmos/Sqlite`); the database name resolves `[UseDatabase("X")]` -> module name from the entity namespace -> `"Default"`.

- **Logical -> physical collapse** (`DataSourceResolver`, singleton): the `DataSources` config section maps logical names to connection strings (plus optional `SQLServerMigrationsAssembly`, `CosmosDatabaseName`). Names without an entry, or matching the top-level `ConnectionStrings` value, collapse onto `Default`; a host with no `DataSources` config behaves exactly like a single-database monolith (one context, FK constraints intact).
- **Eager entity registry** (`EntityDataSourceRegistry`, singleton): maps every entity to its source up front; `DataSourceService` is a facade over it.
- **One context class per engine, one instance per database**: `PhysicalDbContextFactory` (singleton, NEVER pooled) creates raw contexts; `DbContextFactory` (scoped) caches one per `DataSourceKey` and coordinates saves/transactions/disposal. `DataSourceModelCacheKeyFactory` keys EF's model cache by (context type, source name).
- **Cross-source relationships auto-degrade** (`CrossDataSourceDegradeConvention`): FK constraints and navigations are removed when a relationship spans physical sources (scalar FK columns + compensating index survive). Runtime navigation flows through `INavigationPopulator` batch loading; cross-source consistency is the outbox's job. Transactions are per-source, best-effort sequential (no two-phase commit).
- **Design time**: `DesignTimeDbContextHelper.CreateSqlServer(args, ...)` builds a per-source context for `dotnet ef ... -- --datasource <Name>`, enabling one migrations project per database.

**SaveChanges flow**: stamp audit fields -> capture domain events -> serialize to `OutboxMessage` entries -> `base.SaveChangesAsync()` (data + outbox in one transaction) -> dispatch events in-process -> mark outbox processed.

### Outbox Pattern

`OutboxMessage` entries persist atomically with aggregate changes, in the same database as the aggregate (every relational source has its own `OutboxMessages` table; a host drains only its own sources). `OutboxProcessor` wakes on signal or a smart wait (sleeps until the earliest pending row becomes eligible: `Outbox:ProcessingDelaySeconds`, default 5s), falling back to `Outbox:PollingIntervalSeconds` (default 2s; deployed environments set 300s to cut idle polling without adding latency). Batches of 50, up to 5 retries, at-least-once delivery, OpenTelemetry metrics. The poll query runs inside an `OutboxPoll` activity that `OutboxPollFilterProcessor` (Aspire package) suppresses from telemetry export.

### Microservices Extraction Boundaries

A module can be lifted out of the monolith without rewriting application code. The invariant: **application/domain code talks to abstractions; transport choices live at the edges.**

- **Message bus**: `IMessageBus` lives in `MMCA.Common.Application`; Infrastructure supplies `InProcessMessageBus` and `BrokerMessageBus` (MassTransit), selected by `MessageBusSettings`. `Application`/`Domain`/`Shared` must never reference MassTransit directly (`MicroserviceExtractionTests` enforces this).
- **gRPC transport** (`MMCA.Common.Grpc`): `AddGrpcServiceDefaults()` registers server defaults (`GrpcResultExceptionInterceptor` maps `Result` failures to `RpcException`, plus reflection). `AddTypedGrpcClient<TClient>(serviceName)` wires a client to Aspire service discovery over HTTP/2 cleartext (h2c) with a `JwtForwardingClientInterceptor` and the standard Polly pipeline; target services must serve HTTP/2 on their cleartext endpoint (rationale documented in `DependencyInjection.cs`).
- **Cross-service auth (JWKS)**: `IJwksProvider` (`RsaJwksProvider`) exposes signing keys; `JwksEndpointExtensions` serves `/.well-known/jwks.json`; discovery is routed through the gateway.
- **Aspire hosting** (`MMCA.Common.Aspire.Hosting`): AppHost extensions for RabbitMQ broker, JWKS service discovery, gRPC project references.
- **`.Contracts` convention**: any project named `*.Contracts` automatically gets `Grpc.Tools`/`Google.Protobuf` and compiles `Protos/**/*.proto` with `GrpcServices="Both"` (configured in `Directory.Build.props`).

### Other Framework Pieces

- **Push notifications**: SignalR pipeline in Infrastructure (`NotificationHub`, `SignalRPushNotificationSender`, `NullPushNotificationSender` fallback).
- **Idempotency**: `[Idempotent]` attribute; `Idempotency-Key` header; first response cached 24h; duplicates return `X-Idempotent-Replay: true`; per-key `SemaphoreSlim` double-check locking.
- **Aspire package**: `AddServiceDefaults()` configures OpenTelemetry, service discovery, Polly resilience (30s attempt / 60s breaker window / 90s total); `MapDefaultEndpoints()` adds `/health` + `/alive`. The tracing pipeline registers `OutboxPollFilterProcessor`.
- **Testing package**: `IntegrationTestBase<TFixture>` (HTTP client + bearer token + typed helpers + per-test DB reset), `JwtTokenGenerator`. `MMCA.Common.Testing.E2E` is a shipped Playwright fixture package (browser fixtures, Blazor nav helpers, Identity page objects); its Login/Register/Profile workflow bases assert WCAG 2.1 AA via axe-core, and `PlaywrightFixture` selects the engine from `E2E_BROWSER`.

## Testing

xUnit v3 + AwesomeAssertions + Moq + coverlet under Microsoft Testing Platform. Test projects mirror `Source/` under `Tests/`. Test files relax naming and complexity rules via the `.editorconfig` `[Tests/**/*.cs]` section.

## Repository Governance Docs & Commit Convention

- `GETTING-STARTED.md`: step-by-step framework-adoption guide; MMCA.Helpdesk is its runnable companion. Read it before answering "how do I build an app on this framework?".
- `ADRs/`: the canonical, version-controlled ADRs; `ADRs/README.md` owns the count/range and summaries. Read the relevant ADR before changing a pattern it describes; add new ADRs here.
- `ArchitectureEvaluationCriteria.md`: the canonical 34-category two-axis rubric shared by all three repos' scorecards.
- `FACTS.md`: single source of truth for framework-wide facts (version, package list, ADR range, fitness counts). Generated and CI-gated by `build/facts`; never hand-edit computed values. Link to it; do not restate the numbers.
- `ArchitectureScorecard.md` / `RemediationBacklog.md`: the canonical scores and the wave-by-wave remediation log for this repo.
- `VERSIONING.md` (SemVer + lockstep release policy), `CHANGELOG.md`, `SECURITY.md`, `COST.md` (FinOps defaults, rubric §31), `RESPONSIVE.md` (breakpoint/browser matrix, rubric §22).

**Commit-message convention**: remediation work is tagged by scorecard category, `§<m>: <summary>` (e.g. `§30: ...`); update `RemediationBacklog.md` when continuing remediation work.

## Contribution Flow

`main` is server-protected: every change, documentation-only included, lands via branch -> PR -> required checks green (see `CONTRIBUTING.md`) -> squash-merge. Merges here are not deploys. Releases are cut with the workspace `/push-release` flow; the `vX.Y.Z` tag on merged `main` is the only ref pushed directly.

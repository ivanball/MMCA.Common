# CLAUDE.md

This file provides framework-specific guidance to Claude Code (claude.ai/code). Cross-repo conventions (.NET 10 with `LangVersion: preview`, `TreatWarningsAsErrors`, the five error-severity analyzers, Central Package Management, code style and naming, PR-based contribution flow, Microsoft Testing Platform usage) are defined once in the workspace `../CLAUDE.md`: do not re-derive them here.

## Project Overview

MMCA.Common is a .NET 10 NuGet package framework for building modular monolith applications using DDD, Clean Architecture, and CQRS. It publishes its packages to **both nuget.org and GitHub Packages** (ADR-053; see the `release.yml` notes under Build & Test), versioned in lockstep (authoritative package list and count: `FACTS.md`); it is not a runnable app. `MMCA.Common.UI.Maui` is the one MAUI-TFM package: it lives outside `MMCA.Common.slnx` and is built/packed by dedicated windows jobs (ADR-042). The framework also provides the extraction path for lifting modules into standalone microservices (gRPC transport, transport-agnostic message bus, cross-service JWKS auth, Aspire hosting extensions): see "Microservices Extraction Boundaries" below.

## Build & Test

```bash
dotnet build MMCA.Common.slnx -c Release
dotnet test --solution MMCA.Common.slnx -c Release                                # all tests
dotnet test --project Tests/Presentation/MMCA.Common.API.Tests -- --filter-method "*IdempotencyFilterTests*"   # single test (MTP filter; --filter-class works too)
dotnet test --project Tests/Architecture/MMCA.Common.Architecture.Tests           # NetArchTest layer/purity rules (fast, no DB)
dotnet test --project Tests/Presentation/MMCA.Common.UI.E2E.Tests/MMCA.Common.UI.E2E.Tests.csproj      # UI a11y + render smoke (NOT in slnx; needs `playwright install chromium` once)
dotnet run -c Release --project Tests/Performance/MMCA.Common.Benchmarks          # BenchmarkDotNet smoke; append `-- --job Dry` for a fast correctness pass
dotnet pack MMCA.Common.slnx -c Release -o ./nupkgs/
dotnet run --project build/facts -- .                                             # regenerate FACTS.md (CI gates on `--check` drift)
```

CI (`.github/workflows/ci.yml`) runs on **pull requests only** (no `push: main` trigger): `main` is protected with "require branches to be up to date", and a PR check runs against the merge result, which under squash merge is exactly the tree that lands, so a push re-run only re-verified an already-verified tree. Release verification is independent (`release.yml` builds and tests the `v*` tag). A `changes` job classifies the diff first: on a docs-only PR (every changed file `*.md`) the heavy steps are skipped while all required contexts still post green; the FACTS.md drift gate deliberately runs anyway, since FACTS.md is itself markdown. Jobs:
- **build-and-test**: FACTS drift gate (`build/facts -- . --check`) -> restore -> Release build -> vuln audit -> tests under `dotnet-coverage` with `--minimum-expected-tests 2000` (a discovery regression that drops thousands of tests must fail). The vuln audit re-applies the `NuGetAuditSuppress` list from `Directory.Build.props` itself (`dotnet list --vulnerable` ignores suppressions).
- **ui-e2e**: cross-browser matrix (chromium, firefox, and webkit are all required merge gates). Builds the out-of-slnx `MMCA.Common.UI.Gallery` + `MMCA.Common.UI.E2E.Tests` directly by csproj path (deliberately excluded from the slnx to keep unit runs fast) and runs axe-core WCAG 2.1 AA scans + a render smoke against the backend-less gallery.
- **package-consumption**: packs every slnx package into a local folder feed, then restores + builds a throwaway consumer (outside the repo checkout) against those nupkgs, catching pack breaks (NU5xxx) and package-mode-only restore/analyzer failures before a release (source-mode builds mask them).
- **consumer-source-build**: cross-repo canary; checks out MMCA.Helpdesk as a sibling and builds/tests it against THIS PR's framework source via its committed `local.props`, so a breaking public-API change fails pre-merge instead of after a release. Required merge gate.
- **performance-smoke**: runs the BenchmarkDotNet suite (`--job Short`), then `build/perfgate` compares the results against the committed `Tests/Performance/perf-baseline.json` (allocation ceilings + machine-independent ratio floors) and fails on any violation. Moving a number deliberately means updating the baseline file in the same PR.
- **coverage**: merges the tiers (ReportGenerator) and enforces the floor: the unit tier must stay >= 68.3% line coverage (generated `*.g.cs`/`*.generated.cs` excluded).
- **build-maui** (windows runner): builds `MMCA.Common.UI.Maui` across its four TFMs (the out-of-slnx MAUI package, ADR-042); packing happens in `release.yml`. Critical path of the run (~7-8 min), so it caches the MAUI workload packs keyed on the resolved SDK version.
- **sample-deployment-validate**: compiles the `samples/deployment` Bicep templates (`az bicep build`).
- **redis-integration**: runs `MMCA.Common.Infrastructure.Redis.Tests` against a real Redis via Testcontainers (out of the slnx, so the unit loop needs no Docker). `DistributedCacheService` is the one place the storage FORMAT matters: a counter written as a string and read back as a hash round-trips fine against a `Mock<IDistributedCache>` and answers WRONGTYPE against a server.

Versioning is MinVer from git tags (`fetch-depth: 0` required). `release.yml` triggers on `v*` tags: build/test/pack, CycloneDX SBOM as a hard gate, then push to **both** GitHub Packages and nuget.org (ADR-053). The nuget.org push is keyless: the job's OIDC token is exchanged (`NuGet/login`) for a one-hour key, under a trusted-publishing policy pinned to this owner/repo/**workflow file**, so renaming `release.yml` breaks publishing by design. `MMCA.Common.UI.Maui` packs and pushes from a separate `publish-maui` windows job off the same tag.

Gotchas:
- CI runs on Ubuntu: file paths are case-sensitive; match casing exactly.
- Every test project must contain at least one test or MTP fails the build (exit code 8).
- NuGet lock files are committed (`--force-evaluate` to regenerate); `nuget.config` maps everything to nuget.org, so building/testing Common needs NO GitHub token.
- **MassTransit is pinned to v8 by policy** (v9 requires a commercial license); `DependencyVersionTests` fails the build if the major reaches 9. Consumers inherit the pin transitively and deliberately do not subclass `DependencyVersionTestsBase`.

## Source Layout

All `Source/` projects are packable (bulk NuGet metadata via `Directory.Build.props`, versions from MinVer).

```
Source/
  Build/         MMCA.Common.LayerEnforcement.targets (compile-time layer guard)
  Core/          MMCA.Common.{Shared,Domain,Application,Infrastructure}
  Presentation/  MMCA.Common.{API,Grpc,UI,UI.Web} (+ UI.Maui, outside the slnx)
  Hosting/       MMCA.Common.{Aspire,Aspire.Hosting,Gateway,Testing,Testing.E2E,Testing.UI,Testing.Architecture}
Tests/           mirrors Source/ folder for folder, plus Architecture/ (NetArchTest) and Performance/
build/           facts (FACTS.md generator, CI drift gate) and perfgate (benchmark baseline gate)
```

**Folder convention (feature by folder, use case by leaf; rubric §5).** Inside a project the first
folder level names the feature or concern (`Messaging/`, `Notifications/Push/`, `Capabilities/Location/`,
`Notifications/UserNotifications/UseCases/MarkRead/`), technical nouns appear only beneath it, and a
folder holds at most **12 direct code files** (a `.razor` with its `.razor.cs` and `.resx` counts once).
`FolderWidthTests` enforces the cap in CI; its exemption list is the set of deliberately flat public
namespaces every consumer imports (`Application/Interfaces*`, `Application/UseCases*`, `Shared/Auth`,
`API/Startup`, the `MMCA.Common.Testing` root). Namespaces follow folders (IDE0130 is an error), so a
folder move is a public-API rename: land it through the workspace `Tools/Scripts/move-namespace.ps1`
(moves, namespace lines, and every `using` across this repo and the consumers), record it under
**Breaking:** in `CHANGELOG.md`, and sweep the consumers in the same release. Two folders are
folder-only by design and IDE0130-exempt: `Testing.Architecture/Rules/` and `Bases/` keep the flat
`MMCA.Common.Testing.Architecture` namespace consumers subclass (ADR-015). Never name a sub-folder
`Domain`, `Application`, `Infrastructure`, `API` or `UI` inside a module project: `ModuleNameConventions`
derives the module (and so the schema and data-source name) from those namespace segments.

## Architecture

Strict layered dependency flow (each layer references only layers below): `API/Grpc -> Infrastructure -> Application -> Domain -> Shared`. Exceptions: `UI` and `Grpc` depend on **Shared only** (`UI` for Blazor WASM compatibility, `Grpc` because it is pure transport); `UI.Web` is the Blazor Web host layer above both, referencing `UI`, `API`, and `Aspire` directly (transitive `ProjectReference`s disabled, guarded by its own layer-boundary check).

### Architecture Enforcement (two gates)

1. **Compile-time**: `Source/Build/MMCA.Common.LayerEnforcement.targets` (imported for every `Source/` project) fails the build on a forbidden `ProjectReference`.
2. **Runtime**: `Tests/Architecture/MMCA.Common.Architecture.Tests` (NetArchTest) asserts the same rules against compiled assemblies. The rule bodies live once in the `MMCA.Common.Testing.Architecture` package (`ArchitectureRules` + abstract `*TestsBase` parameterized by `IArchitectureMap`); Store and ADC subclass the same bases with their own maps, so rules stay identical across repos.

When moving a type between packages or changing project references, expect both gates to react; add new layer rules in **both** places.

### DI Registration Sequence

Downstream apps register `AddApplicationDecorators()` **last**: Scrutor `TryDecorate` can only wrap handlers already registered, so every module's handler scan must run first. Only that ordering is load-bearing. Reference hosts use `ModuleLoader.DiscoverAndRegister`; the fluent equivalent is `AddApplication() -> AddInfrastructure(config) -> AddAPI(modulesSettings) -> ScanModuleApplicationServices<TModuleRef>() per module -> AddApplicationDecorators()`. Prefer the composition helper `AddMmcaApplicationPipeline(pipeline => ...)`: it runs the sequence in the correct order and seals the pipeline, so any handler registration after `AddApplicationDecorators()` throws instead of silently going unwrapped; `VerifyDecoratorPipeline(IServiceCollection)` exposes the same check to fitness tests.

### CQRS Decorator Pipeline

`ICommandHandler<TCmd, TResult>` / `IQueryHandler<TQuery, TResult>` with decorators registered in `AddApplicationDecorators()` and applied by Scrutor `TryDecorate` in reverse registration order (last registered = outermost). Execution order, outermost to innermost (ADR-014):

```
Commands: FeatureGate -> Authorization -> Logging -> Caching -> Validating -> Timeout -> Transactional -> Handler
Queries:  FeatureGate -> Authorization -> Logging -> Caching -> Validating -> Timeout -> Handler
```

- **FeatureGate**: short-circuits when the command/query's feature flag is off.
- **Authorization**: `IRequiresPermission` commands/queries are checked against `IPermissionRegistry.HasPermission(ICurrentUserService.Roles, ...)`; a denial short-circuits with a `Forbidden` error and increments `cqrs.authorization.denied.count`. Sits outside caching on purpose, so a denied query never reads or populates the cache.
- **Logging**: full pipeline duration via `ICorrelationContext`.
- **Caching**: `ICacheInvalidating` commands invalidate on success (outside the transaction); `IQueryCacheable` queries (with `CacheKey` + `CacheDuration`) cache results.
- **Validating**: FluentValidation before the transaction opens (commands) and before the handler runs (queries). The query decorator sits inside Caching on purpose: a cached entry was already validated when first produced. Queries have no Transactional decorator.
- **Timeout**: `IHasTimeout` commands/queries run under a linked token cancelled after their own budget; expiry returns a `Request.TimedOut` failure and increments `cqrs.timeout.count`, while caller cancellation still propagates as an exception. A budget of zero or less passes through.
- **Transactional**: `ITransactional` commands get a DB transaction; exceptions AND business failures (`Result.Failure`) roll back (atomicity over partial persistence). In-process domain event dispatch is deferred until after a successful commit (`DbContextFactory.ExecuteInTransactionAsync` flushes it post-commit and drops it on rollback), so handlers never act on state that could still roll back; cache invalidation still runs only on success, outside the transaction.

An optional `Profiling` decorator pair is registered by a separate opt-in `AddApplicationProfiling()` call and is not wired by any host today.

### Module System

`IModule` implementations (five members: `Name`, `Dependencies`, `RequiresDependencies`, `Register()`, `RegisterDisabledStubs()`, the last three defaulted, so a leaf module is `Name` plus `Register`) are discovered via reflection and registered in topological order (Kahn's algorithm) by `ModuleLoader`. Seeding is a **separate** contract, `IModuleSeeder.SeedAsync(...)`, which `ModuleLoader` invokes in the same registration order; it is not a member of `IModule`. `ModulesSettings` (config section `Modules`) can disable modules; disabled modules receive stub registrations so cross-module interfaces stay resolvable. `ScanModuleApplicationServices<TAssemblyMarker>()` auto-registers domain event handlers (singleton), DTO/request mappers (scoped), command/query handlers (scoped), and validators. DI registration methods use C# preview extension types (`extension(IServiceCollection services)` blocks in `DependencyInjection.cs`).

### Entity Model

`BaseEntity<TId>` (required init `Id`) -> `AuditableBaseEntity<TId>` (adds `CreatedOn/By`, `LastModifiedOn/By`, `IsDeleted`) -> `AuditableAggregateRootEntity<TId>` (adds domain events, `GetChildOrNotFound<T>()`, `SetItems<T>()`). Aggregates use static `Create(...)` factories returning `Result<T>`; invariants live in static classes composed with `Result.Combine()`. Domain events are collected via `AddDomainEvent()` and dispatched by `DomainEventDispatcher` after `SaveChangesAsync()` (deferred until after commit when a transaction is active; compiled expression delegates, cached per event type; handlers auto-discovered via Scrutor).

Identifier aliases: `GlobalUsings.IdentifierType.cs` (Domain) and `GlobalUsings.NotificationIdentifierType.cs` (Shared) are linked into all projects via `Directory.Build.props`; to add a solution-wide alias, create the `GlobalUsings.*.cs` file and a matching `<Compile Include ... Link=... />` block there.

### Multi-Database Strategy (database per microservice)

Every entity resolves to a physical data source: a `DataSourceKey(Engine, Name)` pair. Engine comes from the configuration base class (`EntityTypeConfigurationSQLServer/Cosmos/Sqlite`); the database name resolves `[UseDatabase("X")]` -> module name from the entity namespace -> `"Default"`.

- **Logical -> physical collapse** (`DataSourceResolver`, singleton): the `DataSources` config section maps logical names to connection strings (plus optional `SQLServerMigrationsAssembly`, `CosmosDatabaseName`). Names without an entry, or matching the top-level `ConnectionStrings` value, collapse onto `Default`; a host with no `DataSources` config behaves exactly like a single-database monolith (one context, FK constraints intact).
- **Eager entity registry** (`EntityDataSourceRegistry`, singleton): maps every entity to its source up front; `DataSourceService` is a facade over it.
- **One context class per engine, one instance per database**: `PhysicalDbContextFactory` (singleton, NEVER pooled) creates raw contexts; `DbContextFactory` (scoped) caches one per `DataSourceKey` and coordinates saves/transactions/disposal. `DataSourceModelCacheKeyFactory` keys EF's model cache by (context type, source name).
- **Cross-source relationships auto-degrade** (`CrossDataSourceDegradeConvention`): FK constraints and navigations are removed when a relationship spans physical sources (scalar FK columns + compensating index survive). Runtime navigation flows through `INavigationPopulator` batch loading; cross-source consistency is the outbox's job. Transactions are per-source, best-effort sequential (no two-phase commit).
- **Design time**: `DesignTimeDbContextHelper.CreateSqlServer(args, ...)` builds a per-source context for `dotnet ef ... -- --datasource <Name>`, enabling one migrations project per database.

**SaveChanges flow**: stamp audit fields -> capture domain events -> serialize to `OutboxMessage` entries -> `base.SaveChangesAsync()` (data + outbox in one transaction) -> dispatch local domain events in-process -> mark their outbox rows processed. Integration events (`IIntegrationEvent`) are NOT dispatched in-process: their outbox rows stay unprocessed and the `OutboxProcessor` publishes them via `IMessageBus`, so the registered transport (in-process or broker) determines delivery. Inside a Transactional command all post-save dispatch is deferred until after commit.

### Outbox Pattern

`OutboxMessage` entries persist atomically with aggregate changes, in the same database as the aggregate (every relational source has its own `OutboxMessages` table; a host drains only its own sources). `OutboxProcessor` wakes on signal or a smart wait (sleeps until the earliest pending row becomes eligible: `Outbox:ProcessingDelaySeconds`, default 5s), falling back to `Outbox:PollingIntervalSeconds` (default 2s; deployed environments set 300s to cut idle polling without adding latency). Batches of 50, up to 5 retries, at-least-once delivery, OpenTelemetry metrics. The poll query runs inside an `OutboxPoll` activity that `OutboxPollFilterProcessor` (Aspire package) suppresses from telemetry export.

### Microservices Extraction Boundaries

A module can be lifted out of the monolith without rewriting application code. The invariant: **application/domain code talks to abstractions; transport choices live at the edges.**

- **Message bus**: `IMessageBus` lives in `MMCA.Common.Application`; Infrastructure supplies `InProcessMessageBus` and `BrokerMessageBus` (MassTransit), selected by `MessageBusSettings`. `Application`/`Domain`/`Shared` must never reference MassTransit directly (`MicroserviceExtractionTests` enforces this).
- **gRPC transport** (`MMCA.Common.Grpc`): `AddGrpcServiceDefaults()` registers server defaults (`GrpcResultExceptionInterceptor` maps `Result` failures to `RpcException`, plus reflection). `AddTypedGrpcClient<TClient>(serviceName)` wires a client to Aspire service discovery over HTTP/2 cleartext (h2c) with a `JwtForwardingClientInterceptor` and the standard Polly pipeline; target services must serve HTTP/2 on their cleartext endpoint (rationale documented in `DependencyInjection.cs`).
- **Cross-service auth (JWKS)**: `IJwksProvider` (`RsaJwksProvider`) exposes signing keys; `JwksEndpointExtensions` serves `/.well-known/jwks.json`; discovery is routed through the gateway. `AddForwardedJwtBearer(authority, audience, configuration, environment, requireHttpsMetadata: null)` resolves `RequireHttpsMetadata` in three steps: explicit argument, then `Authentication:JwtBearer:RequireHttpsMetadata` (`WebApplicationBuilderExtensions.RequireHttpsMetadataConfigKey`), then `true` outside Development; a resolved `false` outside Development is honored (ACA internal-ingress h2c authorities need it) and logs one startup warning naming the key.
- **Aspire hosting** (`MMCA.Common.Aspire.Hosting`): AppHost extensions for RabbitMQ broker, JWKS service discovery, E2E RSA keys, and per-service data sources (`AddMessageBroker`/`WithBroker`/`WithJwksDiscovery`/`WithE2eRsaKeys`/`With{SQLServer,Cosmos,Sqlite}DataSource`). It has no gRPC API; gRPC peers are wired with stock Aspire `WithReference`.
- **`.Contracts` convention**: any project named `*.Contracts` automatically gets `Grpc.Tools`/`Google.Protobuf` and compiles `Protos/**/*.proto` with `GrpcServices="Both"` (configured in `Directory.Build.props`).

### Other Framework Pieces

- **Push notifications**: SignalR pipeline in Infrastructure (`NotificationHub`, `SignalRPushNotificationSender`, `NullPushNotificationSender` fallback).
- **Idempotency**: `[Idempotent]` attribute; `Idempotency-Key` header; first response cached 24h; duplicates return `X-Idempotent-Replay: true`. The cache key is **not** the bare client key: `BuildCacheKey` (`:317-331`) joins subject, request method, route and the client key and hashes them with SHA-256, so two callers cannot collide or replay across each other. Mutual exclusion is an `IDistributedLock` resolved from DI (`AddCaching` registers the SET-NX-PX plus Lua compare-and-delete `RedisDistributedLock` whenever an `IConnectionMultiplexer` is present, else a warn-once `InProcessDistributedLock`); the striped `KeyedSemaphoreStripe` double-check path is only the fallback for a host that registers no lock (ADR-017). A duplicate that cannot take the lock within `LockWait` (5s) gets **409 Conflict**, not a replay. Cached results are 2xx `ObjectResult` **or** body-less 2xx `StatusCodeResult` (a `NoContent()` 204 replays as a bare status code); non-2xx is never cached.
- **Aspire package**: `AddServiceDefaults()` configures OpenTelemetry, service discovery, Polly resilience (30s attempt / 60s breaker window / 90s total); `MapDefaultEndpoints()` adds `/health` + `/alive`. The tracing pipeline registers `OutboxPollFilterProcessor`.
- **Testing package**: `IntegrationTestBase<TFixture>` (HTTP client + bearer token + typed helpers + per-test DB reset), `JwtTokenGenerator`. `MMCA.Common.Testing.E2E` is a shipped Playwright fixture package (browser fixtures, Blazor nav helpers, Identity page objects); its Login/Register/Profile workflow bases assert WCAG 2.1 AA via axe-core, and `PlaywrightFixture` selects the engine from `E2E_BROWSER`.

## Testing

xUnit v3 + AwesomeAssertions + Moq + coverlet under Microsoft Testing Platform. Test projects mirror `Source/` under `Tests/`. Test files relax naming and complexity rules via the `.editorconfig` `[Tests/**/*.cs]` section.

## Repository Governance Docs & Commit Convention

Most documentation moved to its canonical home in the Website repo (`Website/docs-src/`, published at `https://ivanball.github.io/docs/`) on 2026-07-20. What remains here and what moved:
- **Stays in this repo:** `FACTS.md` (single source of truth for framework-wide facts: version, package list, fitness counts; generated and CI-gated by `build/facts`; never hand-edit computed values; link to it, do not restate the numbers), `CHANGELOG.md`, `SECURITY.md`, `NavigationFlow.md` (embedded resource parsed by `NavigationContractTests`, the rubric §25 drift gate: it must stay next to the code), `CONTRIBUTING.md`, `samples/deployment/DEPLOYMENT.md`.
- **Moved to `Website/docs-src/adr/`:** the canonical ADRs; its `README.md` owns the count/range and summaries. Read the relevant ADR before changing a pattern it describes; add new ADRs there (never here).
- **Moved to `Website/docs-src/governance/`:** the 34-category rubric (`ArchitectureEvaluationCriteria.md`) plus this repo's scorecard and remediation backlog (`common-ArchitectureScorecard.md` / `common-RemediationBacklog.md`).
- **Moved to `Website/docs-src/guides/`:** `common-GETTING-STARTED.md` (the six-step `dotnet new mmca-app` adoption path), `common-BUILD-BY-HAND.md` (the same solution phase by phase, for retrofitting an existing solution; MMCA.Helpdesk is its runnable companion), `common-TEMPLATES.md` (the `MMCA.Templates` pack reference), `common-VERSIONING.md` (SemVer + lockstep release policy), `common-COST.md` (FinOps defaults, rubric §31), `common-RESPONSIVE.md` (rubric §22), `common-RESILIENCE.md` (rubric §29), `common-ACCESSIBILITY.md` (rubric §21).
- After editing anything under `Website/docs-src/`, re-render with `cd ../Website/tools && npm run build` and land via a Website PR.

**Commit-message convention**: remediation work is tagged by scorecard category, `§<m>: <summary>` (e.g. `§30: ...`); update the published backlog (`Website/docs-src/governance/common-RemediationBacklog.md`) when continuing remediation work.

## Writing Conventions

Cross-repo prose rules live in the workspace `../CLAUDE.md`. Reinforced here: never use accents, tildes, or em-dashes, and never use the words "seam" or "seams" (banned workspace-wide); prefer "boundary", "extension point", "pipeline", or "layer".

## Contribution Flow

`main` is server-protected: every change, documentation-only included, lands via branch -> PR -> required checks green (see `CONTRIBUTING.md`) -> squash-merge. Merges here are not deploys. Releases are cut with the workspace `/push-release` flow; the `vX.Y.Z` tag on merged `main` is the only ref pushed directly.

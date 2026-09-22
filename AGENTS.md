# AGENTS.md

Framework-specific guidance for every coding agent (Claude Code imports this file from `CLAUDE.md`; Copilot, Codex and Cursor read it directly). Cross-repo conventions (.NET 10 / `LangVersion: preview`, `TreatWarningsAsErrors`, the five analyzers, Central Package Management, code style, PR flow, MTP usage, prose rules) live once in the workspace `../CLAUDE.md`: do not re-derive them here.

## Project Overview

MMCA.Common is a .NET 10 NuGet package framework for modular monolith applications (DDD, Clean Architecture, CQRS). It is not a runnable app. Packages publish to both nuget.org and GitHub Packages (ADR-053) in lockstep; authoritative package list and count: `FACTS.md`. `MMCA.Common.UI.Maui` is the one MAUI-TFM package, outside `MMCA.Common.slnx`, built and packed by dedicated windows jobs (ADR-042).

## Build & Test

```bash
dotnet build MMCA.Common.slnx -c Release
dotnet test --solution MMCA.Common.slnx -c Release
dotnet test --project Tests/Presentation/MMCA.Common.API.Tests -- --filter-method "*IdempotencyFilterTests*"
dotnet test --project Tests/Architecture/MMCA.Common.Architecture.Tests     # NetArchTest layer rules (fast, no DB)
dotnet test --project Tests/Presentation/MMCA.Common.UI.E2E.Tests/MMCA.Common.UI.E2E.Tests.csproj   # NOT in slnx; needs `playwright install chromium` once
dotnet run -c Release --project Tests/Performance/MMCA.Common.Benchmarks    # append `-- --job Dry` for a fast pass
dotnet pack MMCA.Common.slnx -c Release -o ./nupkgs/
dotnet run --project build/facts -- .                                       # regenerate FACTS.md (CI gates on `--check`)
```

### CI (`ci.yml`, pull requests only)

There is no `push: main` trigger: `main` requires an up-to-date branch, so the PR check already ran against the tree that lands. A `changes` job skips heavy steps on a docs-only PR while all required contexts still post green; the FACTS drift gate runs anyway, since FACTS.md is itself markdown. Jobs:

- **build-and-test**: FACTS gate -> restore -> Release build -> vuln audit (re-applies the `NuGetAuditSuppress` list from `Directory.Build.props`, because `dotnet list --vulnerable` ignores suppressions) -> tests with `--minimum-expected-tests 2000`.
- **ui-e2e**: chromium + firefox + webkit, all required. Builds the out-of-slnx `UI.Gallery` + `UI.E2E.Tests` by csproj path (kept out of the slnx to keep unit runs fast) and runs axe-core WCAG 2.1 AA plus a render smoke.
- **package-consumption**: packs every package to a local feed and builds a throwaway consumer outside the checkout, catching NU5xxx and package-mode-only failures that source-mode builds mask.
- **consumer-source-build**: required cross-repo canary; builds MMCA.Helpdesk as a sibling against THIS PR's source via its committed `local.props`, so a breaking public-API change fails pre-merge.
- **performance-smoke**: BenchmarkDotNet `--job Short` then `build/perfgate` against `perf-baseline.json`. Moving a number deliberately means updating the baseline in the same PR.
- **coverage** (unit tier must stay >= 68.3% line), **build-maui** (windows, four TFMs, critical path ~7-8 min, caches workload packs keyed on the resolved SDK version), **sample-deployment-validate**, **redis-integration** (real Redis via Testcontainers, out of the slnx; `DistributedCacheService` is the one place the storage FORMAT matters, since a string-vs-hash mismatch only fails against a real server).

Versioning is MinVer from git tags (`fetch-depth: 0` required). `release.yml` triggers on `v*`: build/test/pack, CycloneDX SBOM as a hard gate, then push to both registries. **The nuget.org push is keyless OIDC under a policy pinned to owner/repo/workflow filename, so renaming `release.yml` breaks publishing by design.** `MMCA.Common.UI.Maui` packs from a separate `publish-maui` windows job off the same tag.

Gotchas:
- CI runs on Ubuntu: file paths are case-sensitive.
- Every test project needs at least one test or MTP fails the build (exit code 8).
- Lock files are committed (`--force-evaluate` to regenerate); `nuget.config` is nuget.org only, so building Common needs NO GitHub token.
- **MassTransit is pinned to v8 by policy** (v9 is commercially licensed); `DependencyVersionTests` fails the build if the major reaches 9. Consumers inherit the pin transitively and deliberately do not subclass `DependencyVersionTestsBase`.

## Source Layout

All `Source/` projects are packable (bulk metadata via `Directory.Build.props`, versions from MinVer).

```
Source/
  Build/         MMCA.Common.LayerEnforcement.targets (compile-time layer guard)
  Core/          MMCA.Common.{Shared,Domain,Application,Infrastructure} (+ AI, the optional
                 language-model boundary on Microsoft.Extensions.AI with no vendor SDK and no MMCA
                 reference, and AI.Anthropic / AI.OpenAI, one IAiProviderFactory each)
  Presentation/  MMCA.Common.{API,Grpc,UI,UI.Web} (+ UI.Maui, outside the slnx)
  Hosting/       MMCA.Common.{Aspire,Aspire.Hosting,Gateway,Testing,Testing.Aspire,Testing.E2E,
                 Testing.UI,Testing.Architecture,AI.Testing}
Tests/           mirrors Source/ plus Architecture/ and Performance/ (+ the AppHost tier and its
                 sample AppHost and service, all three outside the slnx)
build/           facts (FACTS.md generator + drift gate), perfgate (benchmark baseline gate)
```

**Folder convention** (workspace rule; here the first level names the feature or concern: `Messaging/`, `Notifications/Push/`, `Capabilities/Geo/`). `FolderWidthTests` enforces the 12-file cap, exempting three one-concept folders (`Application/UseCases/Decorators`, its test twin, `Domain/Interfaces`). The formerly flat public namespaces were split by concern in a deliberate second pass, not by oversight.

**A folder move here is a public-API rename.** Land it through `Tools/Scripts/move-namespace.ps1`, record it under **Breaking:** in `CHANGELOG.md` AND as a section of `UPGRADING.md` (old-to-new map plus the mechanical fix, identical to the changelog block), state it at the top of the PR description, and sweep the consumers in the same release. Two folders are folder-only by design and IDE0130-exempt: `Testing.Architecture/Rules/` and `Bases/` keep the flat namespace consumers subclass (ADR-015).

## Architecture

Strict layered flow: `API/Grpc -> Infrastructure -> Application -> Domain -> Shared`. Exceptions: `UI` and `Grpc` depend on **Shared only** (UI for Blazor WASM compatibility, Grpc because it is pure transport); `UI.Web` is the Blazor Web host layer above both, referencing `UI`, `API` and `Aspire` directly with transitive `ProjectReference`s disabled.

**Two enforcement gates; add new rules in BOTH:**
1. Compile-time: `Source/Build/MMCA.Common.LayerEnforcement.targets` fails the build on a forbidden `ProjectReference`.
2. Runtime: `Tests/Architecture/` (NetArchTest). Rule bodies live once in the `MMCA.Common.Testing.Architecture` package (`ArchitectureRules` + abstract `*TestsBase` parameterized by `IArchitectureMap`); Store and ADC subclass the same bases.

### DI Registration Sequence

`AddApplicationDecorators()` runs **last**: Scrutor `TryDecorate` only wraps handlers already registered. That ordering is the only load-bearing part. Prefer `AddMmcaApplicationPipeline(pipeline => ...)`: it runs `AddApplication() -> AddInfrastructure(config) -> AddAPI(modulesSettings) -> ScanModuleApplicationServices<TModuleRef>() per module -> AddApplicationDecorators()` in order and **seals** the pipeline, so a later handler registration throws instead of silently going unwrapped. `VerifyDecoratorPipeline(IServiceCollection)` exposes the same check to fitness tests.

### CQRS Decorator Pipeline

Decorators are applied by Scrutor in reverse registration order (last registered = outermost). Execution order (ADR-014):

```
Commands: FeatureGate -> Authorization -> Logging -> Caching -> Validating -> Timeout -> Transactional -> Handler
Queries:  FeatureGate -> Authorization -> Logging -> Caching -> Validating -> Timeout -> Handler
```

- **FeatureGate**: short-circuits on an off flag. Constants live on static `*Features` classes and declare `[FeatureFlag(Permanent|Temporary, RemoveBy, Owner)]` (ADR-031); `FeatureFlagLifecycleTestsBase` fails the build on an undeclared flag and on a temporary one past its `RemoveBy` (the dead-toggle detector).
- **Authorization**: `IRequiresPermission` against `IPermissionRegistry`, then `IRequiresMfa` against the `mfa` claim; denial short-circuits `Forbidden` and increments `cqrs.authorization.denied.count`. Both gates live in the shared `AuthorizationGate` so commands and queries cannot drift. **Sits outside caching on purpose**, so a denied query never reads or populates the cache. Opt-in `AddStoredPermissionGrants(config)` layers a `PermissionGrant` table over the compiled registry (union only, never a stored denial).
- **Logging**: pipeline duration via `ICorrelationContext`. An exception is logged here as a **Warning** outcome line WITHOUT the exception object and rethrown; the handling boundary owns the single Error row with the stack, joined by the correlation id.
- **Caching**: `ICacheInvalidating` commands invalidate on success, outside the transaction; `IQueryCacheable` queries (`CacheKey` + `CacheDuration`) cache results.
- **Validating**: FluentValidation before the transaction opens. The query decorator sits inside Caching on purpose (a cached entry was validated when produced). Queries have no Transactional decorator.
- **Timeout**: `IHasTimeout` runs under a linked token; expiry returns `Request.TimedOut` and increments `cqrs.timeout.count`, while caller cancellation still propagates. A budget <= 0 passes through.
- **Transactional**: exceptions AND `Result.Failure` both roll back (atomicity over partial persistence). In-process domain event dispatch is deferred until after commit, so handlers never act on state that could still roll back.

An optional `Profiling` pair is registered by a separate `AddApplicationProfiling()` and is not wired by any host today.

### Module System

`IModule` has five members (`Name`, `Dependencies`, `RequiresDependencies`, `Register()`, `RegisterDisabledStubs()`, the last three defaulted, so a leaf module is `Name` plus `Register`), discovered by reflection and registered in topological order by `ModuleLoader`. **Seeding is a separate contract**, `IModuleSeeder.SeedAsync(...)`, invoked in the same order; it is not an `IModule` member. `ModulesSettings` can disable modules, which then receive stub registrations so cross-module interfaces stay resolvable. `ScanModuleApplicationServices<TAssemblyMarker>()` auto-registers domain event handlers (singleton), mappers (scoped), handlers (scoped) and validators.

### Entity Model

`BaseEntity<TId>` -> `AuditableBaseEntity<TId>` (audit fields + `IsDeleted`) -> `AuditableAggregateRootEntity<TId>` (domain events, `GetChildOrNotFound<T>()`, `SetItems<T>()`). Aggregates use static `Create(...)` factories returning `Result<T>`; invariants live in static classes composed with `Result.Combine()`. Domain events are collected via `AddDomainEvent()` and dispatched by `DomainEventDispatcher` after `SaveChangesAsync()` (deferred past commit inside a transaction).

Identifier aliases: `GlobalUsings.IdentifierType.cs` (Domain) and `GlobalUsings.NotificationIdentifierType.cs` (Shared) are linked into all projects via `Directory.Build.props`; a new solution-wide alias needs the `GlobalUsings.*.cs` file plus a matching `<Compile Include ... Link=... />` block there.

Strongly typed identifiers are the opt-in alternative (ADR-115): a wrapper is two lines and `services.AddStronglyTypedIds(assembly)` wires JSON, MVC binding, filtering, OpenAPI and EF in one call. The EF half is a **pre-convention type mapping registered once on `ApplicationDbContext.ConfigureConventions`**, so a wrapped property maps to its primitive on all four engines and a wrapped `int` key keeps its store-generated strategy. The aliases remain the default; nothing in `Source/` adopts a wrapper.

### Multi-Database Strategy (database per service)

Every entity resolves to a `DataSourceKey(Engine, Name)`. Engine comes from the configuration base class (`EntityTypeConfigurationSQLServer/PostgreSQL/Cosmos/Sqlite`); the name resolves `[UseDatabase("X")]` -> module name from the entity namespace -> `"Default"`.

- **Four engines, one context class each**: `SQLServerDbContext`, `PostgreSQLDbContext`, `SqliteDbContext`, `CosmosDbContext` (ADR-006/018/113). PostgreSQL takes the SQL Server mapping unchanged, so an entity moves between the two by changing its configuration base class alone; it differs only where the server forces it (partial-index predicates use `"Quoted"` identifiers, the soft-delete predicate is `"IsDeleted" = false`, and every `DateTime` maps `timestamp with time zone` through `UtcDateTimeConverter`, **never** the process-wide `Npgsql.EnableLegacyTimestampBehavior` switch).
- **Logical to physical collapse** (`DataSourceResolver`, singleton): names without a `DataSources` entry, or matching the top-level `ConnectionStrings` value, collapse onto `Default`, so a host with no `DataSources` config behaves exactly like a single-database monolith. A source naming no migrations assembly is created via `EnsureCreated` rather than migrated.
- **One context class per engine, one instance per database**: `PhysicalDbContextFactory` (singleton, **never pooled**) creates raw contexts; `DbContextFactory` (scoped) caches one per `DataSourceKey` and coordinates saves, transactions and disposal. `DataSourceModelCacheKeyFactory` keys EF's model cache by (context type, source name). `EntityDataSourceRegistry` maps every entity to its source up front.
- **Cross-source relationships auto-degrade** (`CrossDataSourceDegradeConvention`): FK constraints and navigations are removed when a relationship spans sources (scalar FK + compensating index survive). Runtime navigation flows through `INavigationPopulator` batch loading; cross-source consistency is the outbox's job. Transactions are per-source, best-effort sequential, no two-phase commit.
- **Design time**: `DesignTimeDbContextHelper.CreateSqlServer(args, ...)` (and the PostgreSQL/Sqlite peers) builds a per-source context for `dotnet ef ... -- --datasource <Name>`.

**SaveChanges flow**: stamp audit fields -> capture domain events -> serialize to `OutboxMessage` rows -> `base.SaveChangesAsync()` (data + outbox in one transaction) -> dispatch local domain events in-process -> mark their outbox rows processed. **Integration events are NOT dispatched in-process**: their rows stay unprocessed and `OutboxProcessor` publishes them via `IMessageBus`. Inside a Transactional command all post-save dispatch is deferred until after commit.

### Outbox and Internal Commands

`OutboxMessage` rows persist atomically with aggregate changes in the same database as the aggregate (every relational source has its own table; a host drains only its own sources). Each row captures the request's ambient context (tenant, user, roles, correlation, trace/span ids), which `OutboxProcessor` restores onto the cycle's scope **per row**, so one row's identity never answers for the next; `BrokerMessageBus` stamps it as the `MMCA-*` headers and `IntegrationEventConsumer` restores it on the far side before touching the inbox. Both hops run through the shared `AmbientOrigin` helper so they cannot drift. The processor wakes on signal or a smart wait (`Outbox:ProcessingDelaySeconds`, default 5s), falling back to `Outbox:PollingIntervalSeconds` (default 2s; deployed environments set 300s). Batches of 50, up to 5 retries, at-least-once. The poll runs inside an `OutboxPoll` activity that `OutboxPollFilterProcessor` suppresses from export.

**Internal commands** are durable deferred work on the same machinery. `IInternalCommandScheduler.ScheduleAsync(command, runAt)` writes its row on the caller's own context, so scheduling inside an `ITransactional` command is atomic with the aggregate change. `InternalCommandProcessor` claims due rows with a lease and executes each in a fresh scope by resolving `ICommandHandler<TCommand, Result>`, so **every decorator applies exactly as it would inline**. The scheduling user, tenant and correlation id are captured and restored (`ScopedUserOverride` decorates `ICurrentUserService`), which is what lets an `IRequiresPermission` command run deferred with truthful audit stamps. A `Result.Failure` and a thrown exception both consume an attempt; retries use exponential backoff with jitter and dead-letter after `MaxAttempts`. **The table is mapped unconditionally, so `InternalCommands:Enabled` is never a migration**, only a choice about whether this host drains the queue.

### Microservices Extraction Boundaries

The invariant: **application and domain code talks to abstractions; transport choices live at the edges.**

- **Message bus**: `IMessageBus` lives in Application; Infrastructure supplies `InProcessMessageBus` and `BrokerMessageBus` (MassTransit), selected by `MessageBusSettings`. Application/Domain/Shared must never reference MassTransit directly (`MicroserviceExtractionTests` enforces this).
- **gRPC** (`MMCA.Common.Grpc`): `AddGrpcServiceDefaults()` for server defaults (`GrpcResultExceptionInterceptor` maps `Result` failures to `RpcException`); `AddTypedGrpcClient<TClient>(serviceName)` wires a client to Aspire service discovery over h2c with a JWT-forwarding interceptor and the standard Polly pipeline, so target services must serve HTTP/2 on their cleartext endpoint. **Consuming modules never touch generated protobuf types**: a hand-written adapter implementing the module's own interface wraps the typed client and IS the Anti-Corruption Layer (ADR-007). The monolith-to-service move follows Strangler Fig (ADR-008).
- **Cross-service auth (JWKS)**: `IJwksProvider` exposes signing keys, `JwksEndpointExtensions` serves `/.well-known/jwks.json`, discovery routes through the gateway. `AddForwardedJwtBearer(...)` resolves `RequireHttpsMetadata` as explicit argument, then config key, then `true` outside Development; **a resolved `false` outside Development is honored** (ACA internal-ingress h2c authorities need it) and logs one startup warning naming the key.
- **Aspire hosting**: `AddMessageBroker`/`WithBroker`/`WithJwksDiscovery`/`WithE2eRsaKeys`/`With{SQLServer,Cosmos,Sqlite}DataSource`. No gRPC API; gRPC peers use stock Aspire `WithReference`.
- **`.Contracts` convention**: any project named `*.Contracts` automatically gets `Grpc.Tools`/`Google.Protobuf` and compiles `Protos/**/*.proto` with `GrpcServices="Both"`.

### Other Framework Pieces

- **Identity completions (all opt-in, ADR-116)**: `AddTwoFactorAuthentication(config)` (RFC 6238; the consumer supplies `ITwoFactorStore` and passes the resolved authenticator to `AuthenticationServiceBase`), `AddEmailConfirmation(config)` (hashed single-use tokens; sign-in gating only when `RequireConfirmedEmail` is set AND the `User` implements `IEmailConfirmableUser`), and `UsersAdminControllerBase<TUserDto>` / `RolesAdminControllerBase`. Every piece is an optional constructor argument or a separate DI call, so a consumer adopting none changes nothing.
- **Push notifications**: SignalR pipeline in Infrastructure (`NotificationHub`, `SignalRPushNotificationSender`, `NullPushNotificationSender` fallback).
- **Managed file storage (ADR-045)**: `IFileStorageService` (`AzureBlobFileStorageService`, `NullFileStorageService` fallback) plus dependency-free upload guards: `ImageContentSniffer`, `DocumentContentSniffer` (accepts only when the BYTES and the extension agree, with a zip-bomb guard on the Office Open XML check), `BlobNames.SanitizeFileName`, and `FileUploadOptions` for the RFC 6266 `Content-Disposition`. The options-carrying `UploadAsync` overload is a **default interface member** so existing implementations keep compiling; implementations should override it.
- **Idempotency**: `[Idempotent]`, `Idempotency-Key` header, 24h replay cache, duplicates return `X-Idempotent-Replay: true`. **The cache key is not the bare client key**: `BuildCacheKey` joins subject, method, route and client key and SHA-256s them, so two callers cannot collide. Mutual exclusion is an `IDistributedLock` from DI (`AddCaching` registers `RedisDistributedLock` when an `IConnectionMultiplexer` is present, else a warn-once in-process lock); the striped double-check path is only the fallback for a host registering no lock (ADR-017). A duplicate that cannot take the lock within `LockWait` (5s) gets **409**, not a replay. Non-2xx is never cached.
- **AI packages (optional, rubric section 16)**: `AddMmcaChatClient(configuration)` binds the `Ai` section and, only when `Ai:Enabled`, registers one `IChatClient` built outermost-first as `BoundedChatClient -> [GuardrailChatClient] -> UsageRecordingChatClient -> [DistributedCache] -> OpenTelemetry -> PromptTagging -> Logging -> provider`. **The provider is an adapter package, never a type the governed package names**: `MMCA.Common.AI.Anthropic` and `MMCA.Common.AI.OpenAI` each register one `IAiProviderFactory` (`AddAnthropicAiProvider()` / `AddOpenAiProvider()`), `Ai:Provider` is a string matched against those names and validated on start, and the `provider` metric tag comes from the client's own `ChatClientMetadata`. `BoundedChatClient` clamps output tokens, enforces a per-call timeout, pins `Ai:Model` (a request naming another model is refused), filters tools through every registered `IChatToolPolicy` (consequential tools also need the request's `mmca.tool.confirmed` stamp; `AllowTools` true with no policy throws at registration), and refuses a request over `PerCallInputTokenBudget` on an ESTIMATE. `GuardrailChatClient` runs the registered `IChatRequestRedactor`s then every `IChatGuardrail` on the request, the response and each streamed update; **`Ai:RequireGuardrail` defaults to true**, so an enabled host with no guardrail throws at registration (`AddPiiRedactionGuardrail()` and `AddContentPolicyGuardrail(configuration)` are the shipped ones; the second binds `Ai:ContentPolicy`, redacts or blocks prompt-injection markers in user-role content and refuses answers matching `BlockedResponsePatterns`). `PromptContract` carries a normalized SHA-256 `Hash`; `MMCA.Common.AI.Testing` ships the harness that keys on it (`ReplayChatClient`, `GoldenReplayTestsBase`, `PromptContractPinTestsBase`), and the AI test project runs both bases over a reference contract so the gate is exercised in this repo's own CI. **The AI packages take no MMCA.Common project reference and nothing in the framework references them** (`AiDependencyIsolationTestsBase` + `EnforceAiLayerBoundary` hold both directions, prefix-matched on `MMCA.Common.AI.`). When `Ai:Enabled` is false nothing is registered, so consumers gate on `GetService<IChatClient>()`, not on a flag.
- **Aspire package**: `AddServiceDefaults()` configures OpenTelemetry, service discovery and Polly resilience (30s attempt / 60s breaker / 90s total); `MapDefaultEndpoints()` adds `/health` + `/alive`. Tracing registers `OutboxPollFilterProcessor` and subscribes the `MMCA.Common.AI` trace source by literal name (Aspire may not reference the AI package; inert without it). Metrics subscribe the nine `MMCA.Common.*` meters plus Polly's own, so `resilience.polly.strategy.events` is exported; Polly's two duration histograms are dropped by a View unless `Telemetry:EnablePollyDurationMetrics=true`.
- **Testing packages**: `IntegrationTestBase<TFixture>` (HTTP client, bearer token, typed helpers, per-test DB reset) and `JwtTokenGenerator`. `MMCA.Common.Testing.E2E` ships Playwright fixtures, Blazor nav helpers and Identity page objects, asserting WCAG 2.1 AA via axe-core with the engine from `E2E_BROWSER`. `MMCA.Common.Testing.Aspire` (ADR-117) is the AppHost tier: `AppHostFixtureBase<TAppHost>` boots a real AppHost and waits for readiness **per resource** (healthy where a health check exists, `Running` where none does), and `AppHostTestBase<TFixture>` carries the wiring assertions. Preconditions are a skip with a reason, not a wedge (`AppHostEnvironmentGate` needs `MMCA_APPHOST_TESTS=1` plus Docker and the dev certificate where declared). **Never gate a startup wait on readiness**; `/alive` is the startup signal.

## Testing

xUnit v3 + AwesomeAssertions + Moq + coverlet under MTP. Test projects mirror `Source/` under `Tests/`; test files relax naming and complexity rules via the `.editorconfig` `[Tests/**/*.cs]` section.

## Governance Docs & Commit Convention

The documentation library is canonical in the Website repo (`../Website/docs-src/`). Docs-src edits are re-rendered and land via a Website PR.

- **In this repo**: `FACTS.md` (generated and CI-gated by `build/facts`; never hand-edit computed values, link to it rather than restating numbers), `CHANGELOG.md`, `UPGRADING.md`, `SECURITY.md`, `CONTRIBUTING.md`, `samples/deployment/DEPLOYMENT.md`, and `NavigationFlow.md` (an embedded resource parsed by `NavigationContractTests`, so **it must stay next to the code**).
- **`../Website/docs-src/`**: `adr/` (its `README.md` owns the count and range; **add new ADRs there, never here**), `governance/` (the 34-category rubric plus `common-ArchitectureScorecard.md` / `common-RemediationBacklog.md`), `guides/` (`common-GETTING-STARTED.md`, `common-BUILD-BY-HAND.md`, `common-TEMPLATES.md`, `common-VERSIONING.md`, `common-COST.md`, `common-RESPONSIVE.md`, `common-RESILIENCE.md`, `common-ACCESSIBILITY.md`).

**Commit convention**: remediation work is tagged by scorecard category, `§<m>: <summary>`; update `common-RemediationBacklog.md` when continuing remediation work.

## Contribution Flow

`main` is server-protected: every change, documentation-only included, lands via branch -> PR -> required checks green -> squash-merge. Merges here are not deploys. Releases are cut with `/push-release`; the `vX.Y.Z` tag on merged `main` is the only ref pushed directly.

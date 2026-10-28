# AGENTS.md

Framework-specific guidance for every coding agent (Claude Code imports this file from `CLAUDE.md`; Copilot, Codex and Cursor read it directly). Cross-repo conventions (.NET 10 / `LangVersion: preview`, `TreatWarningsAsErrors`, the five analyzers, Central Package Management, code style, PR flow, MTP usage, prose rules) live once in the workspace `../CLAUDE.md`: do not re-derive them here.

This file holds the rules you act on. How each mechanism works lives in the ADR cited beside it (`../Website/docs-src/adr/`, published at <https://ivanball.github.io/docs/>), in `../Website/docs-src/guides/common-CAPABILITIES.md` for the opt-in capabilities, and in the source itself: read those before changing a mechanism, not this file.

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

- **build-and-test**: FACTS gate, Release build, vuln audit (re-applies the `NuGetAuditSuppress` list from `Directory.Build.props`), tests with `--minimum-expected-tests 2000`.
- **ui-e2e**: chromium + firefox + webkit, all required; builds the out-of-slnx `UI.Gallery` + `UI.E2E.Tests` by csproj path and asserts axe-core WCAG 2.1 AA.
- **package-consumption**: packs every package and builds a throwaway consumer, catching package-mode-only failures that source mode masks. Run it locally with `pwsh build/consumer-check/Invoke-ConsumerCheck.ps1` (one `-Stage` per step).
- **consumer-source-build**: builds MMCA.Helpdesk against THIS PR's source, so a breaking public-API change fails pre-merge.
- **performance-smoke**: `build/perfgate` against `perf-baseline.json` (ADR-060). Moving a number deliberately means updating the baseline in the same PR.
- **coverage**: the unit tier must stay >= 68.3% line.
- Also run: **build-maui** (windows, four TFMs), **sample-deployment-validate**, and the Testcontainers tiers **redis-integration**, **sqlserver-integration**, **aws-integration** (LocalStack) and **gcp-integration** (emulators), all out of the slnx; the last three are not required checks.

Versioning is MinVer from git tags (`fetch-depth: 0` required). `release.yml` triggers on `v*` (build/test/pack, CycloneDX SBOM as a hard gate, push to both registries); `MMCA.Common.UI.Maui` packs from a separate `publish-maui` job off the same tag. **The nuget.org push is keyless OIDC pinned to the workflow filename, so never rename `release.yml`.** The last commit before the tag runs `pwsh build/publicapi/Promote-PublicApi.ps1` (`-WhatIf` first) to move every `PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt`.

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
  Core/          MMCA.Common.{Shared,Domain,Application,Infrastructure}, cloud-neutral (+ AI, the
                 optional language-model boundary on Microsoft.Extensions.AI with no vendor SDK and
                 no MMCA reference, and AI.Anthropic / AI.OpenAI, one IAiProviderFactory each)
  Presentation/  MMCA.Common.{API,Grpc,UI,UI.Web} (+ UI.Maui, outside the slnx)
  Hosting/       MMCA.Common.{Aspire,Aspire.Hosting,Gateway,Testing,Testing.Aspire,Testing.E2E,
                 Testing.UI,Testing.Architecture,AI.Testing} (+ Azure, Aws and Testing.Azure, the
                 cloud vendor adapters)
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

`AddApplicationDecorators()` runs **last**: Scrutor `TryDecorate` only wraps handlers already registered. That ordering is the only load-bearing part. Prefer `AddMmcaApplicationPipeline(pipeline => ...)`: it runs `AddApplication() -> the configure callback (module scans, `ModuleLoader.DiscoverAndRegister`, broker registration) -> AddApplicationDecorators()` in order and **seals** the pipeline (`AddInfrastructure`, `AddAPI` and other non-handler registrations stay outside the call, since their order relative to the decorators does not matter), so a later handler registration throws instead of silently going unwrapped. `VerifyDecoratorPipeline(IServiceCollection)` exposes the same check to fitness tests.

### CQRS Decorator Pipeline

Decorators are applied by Scrutor in reverse registration order (last registered = outermost). Execution order (ADR-014):

```
Commands: FeatureGate -> Authorization -> Logging -> Caching -> Validating -> Timeout -> Transactional -> Handler
Queries:  FeatureGate -> Authorization -> Logging -> Caching -> Validating -> Timeout -> Handler
```

The positions are deliberate; keep them when adding or moving a decorator:

- **FeatureGate**: every flag constant declares `[FeatureFlag(Permanent|Temporary, RemoveBy, Owner)]` on a static `*Features` class; `FeatureFlagLifecycleTestsBase` fails the build on an undeclared flag or a temporary one past `RemoveBy` (ADR-031).
- **Authorization** sits outside Caching so a denied query never reads or populates the cache. Command and query checks share `AuthorizationGate` so they cannot drift (ADR-020).
- **Logging** logs an exception as a Warning outcome line WITHOUT the exception object and rethrows; the handling boundary owns the single Error row with the stack.
- **Caching** invalidates `ICacheInvalidating` commands on success, outside the transaction.
- **Validating** runs before the transaction opens; the query decorator sits inside Caching (a cached entry was validated when produced).
- **Transactional** rolls back on an exception AND on `Result.Failure`, and defers in-process domain event dispatch until after commit. Queries have no Transactional decorator.

An optional `Profiling` pair (`AddApplicationProfiling()`) is not wired by any host today.

### Module System

A leaf `IModule` is `Name` plus `Register()` (the other members are defaulted), discovered by reflection and registered in topological order by `ModuleLoader` (ADR-059). **Seeding is a separate contract** (`IModuleSeeder.SeedAsync`), not an `IModule` member. A module disabled in `ModulesSettings` gets `RegisterDisabledStubs()` registrations so cross-module interfaces stay resolvable. `ScanModuleApplicationServices<TAssemblyMarker>()` auto-registers event handlers, mappers, handlers and validators.

### Entity Model

`BaseEntity<TId>` -> `AuditableBaseEntity<TId>` (audit fields + `IsDeleted`) -> `AuditableAggregateRootEntity<TId>` (domain events, `GetChildOrNotFound<T>()`, `SetItems<T>()`). Aggregates use static `Create(...)` factories returning `Result<T>`; invariants live in static classes composed with `Result.Combine()`. Domain events are collected via `AddDomainEvent()` and dispatched by `DomainEventDispatcher` after `SaveChangesAsync()` (deferred past commit inside a transaction).

Identifier aliases: `GlobalUsings.IdentifierType.cs` (Domain) and `GlobalUsings.NotificationIdentifierType.cs` (Shared) are linked into all projects via `Directory.Build.props`; a new solution-wide alias needs the `GlobalUsings.*.cs` file plus a matching `<Compile Include ... Link=... />` block there.

Strongly typed identifiers are the opt-in alternative (`services.AddStronglyTypedIds(assembly)`, ADR-115). The aliases remain the default; nothing in `Source/` adopts a wrapper.

### Multi-Database Strategy (database per service)

Every entity resolves to a `DataSourceKey(Engine, Name)`. Engine comes from the configuration base class (`EntityTypeConfigurationSQLServer/PostgreSQL/Cosmos/Sqlite`); the name resolves `[UseDatabase("X")]` -> module name from the entity namespace -> `"Default"`.

- **Four engines, one context class each** (`SQLServerDbContext`, `PostgreSQLDbContext`, `SqliteDbContext`, `CosmosDbContext`; ADR-006/018/113), one instance per database. Never add a per-module context class, and never pool `PhysicalDbContextFactory`.
- **PostgreSQL takes the SQL Server mapping unchanged**; an entity moves between the two by its configuration base class alone. Where the server forces a difference, partial-index predicates use `"Quoted"` identifiers (soft delete is `"IsDeleted" = false`) and `DateTime` maps through `UtcDateTimeConverter`, **never** the process-wide `Npgsql.EnableLegacyTimestampBehavior` switch.
- **A host with no `DataSources` config is a single-database monolith**: unmapped names collapse onto `Default` (`DataSourceResolver`).
- **Cross-source relationships auto-degrade** to a scalar FK plus index (`CrossDataSourceDegradeConvention`). Cross-source reads go through `INavigationPopulator` (ADR-002), cross-source consistency is the outbox's job, and there is no two-phase commit.
- **Design time**: `DesignTimeDbContextHelper.CreateSqlServer(args, ...)` (and its PostgreSQL/Sqlite peers) for `dotnet ef ... -- --datasource <Name>`.

**SaveChanges flow**: stamp audit fields, capture domain events into `OutboxMessage` rows, save data + outbox in one transaction, then dispatch local domain events in-process. **Integration events are NOT dispatched in-process**: `OutboxProcessor` publishes their rows via `IMessageBus` (ADR-003).

### Outbox and Internal Commands

`OutboxMessage` rows live in the aggregate's own database and a host drains only its own sources (ADR-003, ADR-100). Delivery is at-least-once, so consumers dedupe through the inbox (ADR-021). Each row carries the request's ambient context (tenant, user, permissions, correlation; never the `mfa` claim), restored onto a fresh scope **per row**. The outbox hop and the broker hop (`MMCA-*` headers, restored by `IntegrationEventConsumer`) both go through `AmbientOrigin`: change ambient propagation there, never in one hop alone.

**Internal commands** (ADR-114) are durable deferred work on the same machinery. `IInternalCommandScheduler.ScheduleAsync` writes on the caller's own context, so scheduling inside an `ITransactional` command is atomic with the aggregate change, and the processor resolves the normal `ICommandHandler`, so **every decorator applies as it would inline**. **The table is mapped unconditionally, so `InternalCommands:Enabled` is never a migration**, only whether this host drains the queue.

### Microservices Extraction Boundaries

The invariant: **application and domain code talks to abstractions; transport choices live at the edges.**

- **Message bus**: Application/Domain/Shared never reference MassTransit (`MicroserviceExtractionTests`). `IMessageBus` is selected by the string `MessageBus:Provider`; `InProcess`, `RabbitMq` and `PostgreSql` are built in, and any other value names an `IBrokerTransport` an adapter registers BEFORE `AddBrokerMessaging` (ADR-066, ADR-118). Retry, redelivery and backpressure live once in `BrokerTransportPolicies`, shared by every transport.
- **gRPC**: **consuming modules never touch generated protobuf types**; a hand-written adapter implementing the module's own interface wraps the typed client and IS the Anti-Corruption Layer (ADR-007, ADR-012). `AddTypedGrpcClient<TClient>` dials over h2c, so a target service must serve HTTP/2 on its cleartext endpoint. The monolith-to-service move follows Strangler Fig (ADR-008).
- **Cross-service auth**: signing keys are served at `/.well-known/jwks.json`; `AddForwardedJwtBearer` honors a configured `RequireHttpsMetadata=false` outside Development (with one startup warning) because ACA internal ingress needs it.
- **`.Contracts` convention**: any project named `*.Contracts` automatically gets `Grpc.Tools`/`Google.Protobuf` and compiles `Protos/**/*.proto` with `GrpcServices="Both"`.

### Other Framework Pieces

Every piece below is opt-in, so a consumer adopting none changes nothing; keep new ones that way (an optional constructor argument or a separate DI call). Registration calls and options: `common-CAPABILITIES.md`.

- **Identity completions** (two-factor, email confirmation, user and role admin controllers): ADR-116.
- **Native push and file storage** default to inert `Null*` implementations that a vendor adapter call replaces (ADR-044, ADR-045). Every `IFileStorageService` implements the options-carrying `UploadAsync` overload and honors its headers; the upload sniffers accept only when the BYTES and the extension agree.
- **Installable web app** (ADR-136): **the service worker never caches a page or a response**, because server-rendered pages carry the signed-in user's data. `Pwa:ServiceWorker:Enabled=false` is the kill switch.
- **Idempotency** (ADR-017): the replay cache key is a SHA-256 of subject, method, route and client key, **never the bare client key**. A duplicate that cannot take the lock within `LockWait` gets 409, not a replay; non-2xx is never cached.
- **AI packages** (ADR-120): **the AI packages take no MMCA.Common project reference and nothing in the framework references them** (`AiDependencyIsolationTestsBase` + `EnforceAiLayerBoundary`; a dotted NetArchTest namespace entry matches nothing, so match on the `MMCA.Common.AI` segment). The provider is an adapter package (`MMCA.Common.AI.Anthropic`, `MMCA.Common.AI.OpenAI`), never a type the governed package names. `Ai:RequireGuardrail` defaults to true, so an enabled host with no guardrail throws at registration. With `Ai:Enabled` false nothing is registered: gate on `GetService<IChatClient>()`, not on a flag.
- **Cloud vendor adapters** (ADR-134): **the core packages reference no cloud vendor SDK**. Vendor code lives in `MMCA.Common.Azure`, `MMCA.Common.Aws`, `MMCA.Common.Gcp` and `MMCA.Common.Testing.Azure`, one registration call per capability, and adapters reference the core, never the reverse (`CloudVendorIsolationTestsBase` + `EnforceCloudVendorBoundary`). Exactly two references are exempt, both database engines: `Microsoft.EntityFrameworkCore.Cosmos` in Infrastructure and `Aspire.Hosting.Azure.CosmosDB` in Aspire.Hosting. **Configuration that asks for a vendor capability without its adapter call fails startup, naming the call** (`AddInfrastructure`, `AddServiceDefaults()`); give any new vendor-gated config key the same check. PostgreSQL connection customization goes through `AddNpgsqlDataSourceConfigurator`.
- **Aspire package** (ADR-041): call vendor exporters (`AddAzureMonitorExporter()`, `AddGoogleCloudTelemetryExporter()`) after `AddServiceDefaults()` so the filter processors run first. Aspire subscribes the `MMCA.Common.AI` trace source by literal name because it may not reference the AI package.
- **Testing packages**: `IntegrationTestBase<TFixture>`, `MMCA.Common.Testing.E2E` (Playwright + axe-core, engine from `E2E_BROWSER`) and `MMCA.Common.Testing.Aspire` (ADR-117). In the AppHost tier a missing precondition is a skip with a reason, not a wedge, and **never gate a startup wait on readiness**: `/alive` is the startup signal.

## Testing

xUnit v3 + AwesomeAssertions + Moq + coverlet under MTP. Test projects mirror `Source/` under `Tests/`; test files relax naming and complexity rules via the `.editorconfig` `[Tests/**/*.cs]` section.

## Governance Docs & Commit Convention

The documentation library is canonical in the Website repo (`../Website/docs-src/`). Docs-src edits are re-rendered and land via a Website PR.

- **In this repo**: `FACTS.md` (generated and CI-gated by `build/facts`; never hand-edit computed values, link to it rather than restating numbers), `CHANGELOG.md`, `UPGRADING.md`, `SECURITY.md`, `CONTRIBUTING.md`, `samples/deployment/DEPLOYMENT.md`, and `NavigationFlow.md` (an embedded resource parsed by `NavigationContractTests`, so **it must stay next to the code**).
- **`../Website/docs-src/`**: `adr/` (its `README.md` owns the count and range; **add new ADRs there, never here**), `governance/` (the 34-category rubric plus `common-ArchitectureScorecard.md` / `common-RemediationBacklog.md`), `guides/` (the `common-*.md` files).

**Commit convention**: remediation work is tagged by scorecard category, `§<m>: <summary>`; update `common-RemediationBacklog.md` when continuing remediation work.

## Contribution Flow

`main` is server-protected: every change, documentation-only included, lands via branch -> PR -> required checks green -> squash-merge. Merges here are not deploys. Releases are cut with `/push-release`; the `vX.Y.Z` tag on merged `main` is the only ref pushed directly.

# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

MMCA.Common is a .NET 10.0 NuGet package framework for building modular monolith applications using DDD, Clean Architecture, and CQRS patterns. It publishes thirteen NuGet packages to GitHub Packages — it is not a runnable app itself.

The framework also provides the seams for **extracting modules into standalone microservices** (gRPC transport, a transport-agnostic message bus, cross-service JWKS auth, and Aspire hosting extensions). See "Microservices Extraction Seams" below — much of the recent work targets this path.

## Build & Test Commands

```bash
# Build
dotnet build MMCA.Common.slnx -c Release

# Test (all projects — uses Microsoft Testing Platform via global.json)
dotnet test --solution MMCA.Common.slnx -c Release

# Test a single project
dotnet test --project Tests/Presentation/MMCA.Common.API.Tests

# Test a specific test class or method
dotnet test --project Tests/Presentation/MMCA.Common.API.Tests -- -method "*IdempotencyFilterTests*"

# Architecture tests only (NetArchTest layer/purity rules — fast, no DB)
dotnet test --project Tests/Architecture/MMCA.Common.Architecture.Tests

# UI accessibility + render smoke (Playwright/axe — NOT in the .slnx; needs `playwright install chromium` once)
dotnet test --project Tests/Presentation/MMCA.Common.UI.E2E.Tests/MMCA.Common.UI.E2E.Tests.csproj

# Pack NuGet packages
dotnet pack MMCA.Common.slnx -c Release -o ./nupkgs/
```

CI (`.github/workflows/ci.yml`) runs two jobs on every push/PR to `main`:
- **`build-and-test`** — `restore → build -c Release → vuln-audit → test --minimum-expected-tests 1`. The `--minimum-expected-tests 1` guard fails the run if any test project discovers zero tests (Microsoft Testing Platform otherwise exits 8). The **vuln-audit** step runs `dotnet list package --vulnerable --include-transitive` and fails the build if any vulnerable package is reported.
- **`ui-e2e`** (*UI a11y + render smoke*) — builds `Tests/Presentation/MMCA.Common.UI.E2E.Tests` directly (it and `MMCA.Common.UI.Gallery` are **deliberately outside `MMCA.Common.slnx`** so the unit-test job stays fast), installs Playwright chromium, then runs the suite. It self-hosts the backend-less gallery and scans the real Login/Register pages + primitives showcase with **axe-core (WCAG 2.1 AA)** plus a render smoke. To reproduce locally, build/test those csproj paths directly — `dotnet test --solution` will not touch them.

Versioning uses MinVer (derived from git tags). CI requires `fetch-depth: 0` for full git history. Release workflow triggers on `v*` tags, extracts version, packs, and pushes to GitHub Packages.

Central package management is enabled — all package versions live in `Directory.Packages.props`. When adding or updating a NuGet package, update the version there (not in individual `.csproj` files). NuGet **lock files** are committed (`RestorePackagesWithLockFile`) for reproducible restores, and `nuget.config` pins `packageSourceMapping` to nuget.org — MMCA.Common restores entirely from nuget.org (no `GITHUB_TOKEN` needed to build/test). **`MassTransit` is pinned to v8 by policy** (v9 requires a commercial license); `DependencyVersionTests` parses `Directory.Packages.props` and fails the build if the MassTransit major reaches 9 — bumping it is not a "just update the version" change.

CI runs on **Ubuntu** — file paths are case-sensitive. Match casing exactly in file/folder references. Every test project must contain at least one test or Microsoft Testing Platform will fail the build (exit code 8).

## Source Layout

```
Source/
├── Build/
│   └── MMCA.Common.LayerEnforcement.targets  # Compile-time layer guard (see Architecture Enforcement)
├── Core/
│   ├── MMCA.Common.Shared           # Result pattern, errors, DTOs, value objects
│   ├── MMCA.Common.Domain           # Entities, aggregates, domain events, specifications
│   ├── MMCA.Common.Application      # CQRS handlers, decorators, module system, validation, IMessageBus
│   └── MMCA.Common.Infrastructure   # EF Core, repositories, UoW, caching, outbox, JWT, JWKS, message bus, SignalR
├── Presentation/
│   ├── MMCA.Common.API              # Controllers, middleware, idempotency, error mapping, JWKS endpoint
│   ├── MMCA.Common.Grpc             # gRPC server defaults, Result↔RpcException, JWT-forwarding client interceptor
│   └── MMCA.Common.UI               # Blazor components, MudBlazor theme, HTTP resilience
└── Hosting/
    ├── MMCA.Common.Aspire           # Service defaults, OpenTelemetry, health checks
    ├── MMCA.Common.Aspire.Hosting   # AppHost extensions: RabbitMQ, JWKS discovery, gRPC project wiring
    ├── MMCA.Common.Testing          # Integration test base, JWT generator, fixtures
    ├── MMCA.Common.Testing.E2E      # Playwright E2E fixtures, Blazor nav helpers, page objects
    ├── MMCA.Common.Testing.UI       # bUnit component-test base, MudBlazor provider harness, interaction helpers
    └── MMCA.Common.Testing.Architecture  # IArchitectureMap + reusable NetArchTest rule library + abstract test bases (consumed by each repo's *.Architecture.Tests)

Tests/                               # Mirrors Source/ structure
├── Core/           (Shared.Tests, Domain.Tests, Application.Tests, Infrastructure.Tests)
├── Presentation/   (API.Tests, Grpc.Tests, UI.Tests — all in the .slnx)
│                   (UI.Gallery + UI.E2E.Tests — NOT in the .slnx; see below)
├── Hosting/        (Aspire.Tests)
└── Architecture/   (Architecture.Tests — NetArchTest layer/purity/extraction rules)
```

`Tests/Presentation/MMCA.Common.UI.Gallery` (a backend-less Blazor host rendering the real Login/Register pages + a primitives showcase) and `MMCA.Common.UI.E2E.Tests` (Playwright axe/render-smoke) are **intentionally excluded from `MMCA.Common.slnx`** so `dotnet build`/`dotnet test --solution` stay fast. They reference MMCA.Common source projects directly (no GitHub Packages token needed) and only run in CI's `ui-e2e` job. Build/test them by csproj path.

All thirteen `Source/` projects are packable (each has a `PackageId`); NuGet metadata is applied in bulk via `Directory.Build.props` to any project under `Source/`. Versions come from MinVer (a `MinVer` `PackageReference` is present in each packable project).

## Architecture

Strict layered dependency flow — each layer only references layers below it:

```
API / Grpc       (presentation/transport)
     ↓
Infrastructure   (EF Core, caching, JWT, JWKS, outbox, message bus, SignalR)
     ↓
Application      (CQRS handlers, decorators, module system, IMessageBus)
     ↓
Domain           (entities, aggregates, domain events, specifications)
     ↓
Shared           (Result pattern, errors, DTOs, value objects)
```

`UI` and `Grpc` are exceptions: both depend on **`Shared` only** — `UI` for Blazor WASM compatibility, `Grpc` because it is pure transport infrastructure that must not couple to Domain/Application/Infrastructure.

### Architecture Enforcement (two layers)

The dependency rules above are not just convention — they are enforced twice:

1. **Compile-time** — `Source/Build/MMCA.Common.LayerEnforcement.targets` (imported from `Directory.Build.props` for every `MMCA.Common.*` project under `Source/`). It inspects `ProjectReference`s in a `BeforeTargets="ResolveProjectReferences"` step and **fails the build** with a descriptive error if a layer references a forbidden upstream layer.
2. **Runtime** — `Tests/Architecture/MMCA.Common.Architecture.Tests` (NetArchTest.eNhancedEdition) asserts the same rules against compiled assembly dependencies. The rule bodies live **once** in the `MMCA.Common.Testing.Architecture` package (`ArchitectureRules` + abstract `*TestsBase` classes parameterized by `IArchitectureMap`); each test class is a sealed subclass supplying `CommonArchitectureMap` (one anchor type per package). MMCA.Store and MMCA.ADC consume the same package and supply their own maps, so the rules stay identical across all three repos. (`FrameworkSanityTests` holds the few Common-only checks — the `MMCA.Common.Grpc` boundary and `IMessageBus`/`IJwksProvider` placement.)

When changing project references or moving a type between packages, expect both gates to react. Add new layer rules in **both** places.

### DI Registration Sequence

Downstream apps must register services in this order (decorators require existing handler registrations):

```csharp
services.AddApplication()                                    // Core services, event dispatcher
    .ScanModuleApplicationServices<ModuleAClassRef>()        // Module A handlers, validators, mappers
    .ScanModuleApplicationServices<ModuleBClassRef>()        // Module B handlers, validators, mappers
    .AddApplicationDecorators()                              // MUST be last — Scrutor TryDecorate wraps existing handlers
    .AddInfrastructure(configuration)                        // Repos, UoW, DbContexts, caching, outbox
    .AddAPI(modulesSettings);                                // Controllers, idempotency, exception handlers
```

### Result Pattern

`Result<T>` with `Error`/`ErrorType` instead of exceptions for flow control. Supports `Match()`, `Map()`, `BindAsync()` combinators. `ApiControllerBase.HandleFailure()` maps `ErrorType` to HTTP status codes via `FrozenDictionary`.

### CQRS Decorator Pipeline

`ICommandHandler<TCmd, TResult>` and `IQueryHandler<TQuery, TResult>` with decorator pipeline. Decorators wrap handlers in this execution order:

```
Logging → Caching → Transactional → Concrete Handler
```

Key behaviors:
- **Transactional**: Commands implementing `ITransactional` get a DB transaction. Exceptions trigger rollback.
- **Caching**: Commands implementing `ICacheInvalidating` invalidate cache on success (outside transaction boundary). Queries use `IQueryCacheKeyProvider`.
- **Logging**: Logs full pipeline duration via `ICorrelationContext`.
- Business failures (`Result.Failure`) commit the transaction but skip cache invalidation.

### Module System

`IModule` implementations are auto-discovered and registered in **topological order** (Kahn's algorithm) based on declared `Dependencies`. Modules declare a `Name` and optionally `RequiresDependencies`. `ModulesSettings` (config section `"Modules"`) can disable modules — disabled modules receive stub registrations so cross-module interfaces remain resolvable.

Convention scanning via `ScanModuleApplicationServices<TAssemblyMarker>()` auto-registers domain event handlers (singleton), DTO/request mappers (scoped), command/query handlers (scoped), and FluentValidation validators.

### Entity Model

```
BaseEntity<TId> → AuditableBaseEntity<TId> → AuditableAggregateRootEntity<TId>
```

- `BaseEntity<TId>`: `required init Id` property, EF materializes via parameterless constructor
- `AuditableBaseEntity<TId>`: adds `CreatedOn/By`, `LastModifiedOn/By` (stamped automatically by `ApplicationDbContext.SaveChangesAsync`)
- `AuditableAggregateRootEntity<TId>`: adds domain events collection, `GetChildOrNotFound<T>()`, `SetItems<T>()` with `ValidateSetItems()` hook
- **Soft-delete**: `IsDeleted` flag with EF global query filters — entities are never hard-deleted

### Entity Identifier Convention

A shared `global using UserIdentifierType = int;` in `Source/Core/MMCA.Common.Domain/GlobalUsings.IdentifierType.cs` is linked into all MMCA.Common projects via `Directory.Build.props`. A second alias file, `GlobalUsings.NotificationIdentifierType.cs` (in `MMCA.Common.Shared`), is linked the same way. To add a solution-wide identifier alias, create the `GlobalUsings.*.cs` file and add a matching `<Compile Include ... Link=... />` block in `Directory.Build.props`.

### Multi-Database Strategy (database per microservice)

Every entity resolves to a **physical data source** — a `DataSourceKey(Engine, Name)` pair, where the engine comes from the configuration base class (`EntityTypeConfigurationSQLServer/Cosmos/Sqlite`, via `[UseDataSource]`) and the database name resolves as: `[UseDatabase("X")]` attribute on the concrete configuration → module name derived from the entity namespace (segment before `Domain`, same rule as SQL schema derivation) → `"Default"`.

- **Logical → physical collapse** (`DataSourceResolver`, singleton): the `DataSources` appsettings section maps logical names to connection strings (`DataSources:Conference:SQLServerConnectionString`, optional per-source `SQLServerMigrationsAssembly`, `CosmosDatabaseName`). Logical names without an entry, or whose connection string equals the top-level `ConnectionStrings` value, collapse onto the `Default` source — so a host with no `DataSources` config behaves exactly like a single-database monolith (one context, one change tracker, FK constraints intact). Names sharing a connection string collapse to one physical source. Conflicting `SQLServerMigrationsAssembly` declarations on a collapsed source fail at startup.
- **Eager entity registry** (`EntityDataSourceRegistry`, singleton): scans configuration assemblies up front and maps every entity to its physical source — routing no longer depends on a model having been built. `DataSourceService` is a facade over it.
- **One context class per engine, one instance per database**: `PhysicalDbContextFactory` (singleton, NEVER pooled — per-instance `PhysicalDataSource` state) creates raw contexts; `DbContextFactory` (scoped) caches one per `DataSourceKey` and coordinates saves/transactions/disposal. `DataSourceModelCacheKeyFactory` keys EF's model cache by (context type, source name) so each database gets its own model containing only its own entities.
- **Cross-source relationships auto-degrade** (`CrossDataSourceDegradeConvention`, model-finalizing): when a relationship's ends live in different physical sources, the FK constraint and navigations are removed from the model (scalar FK columns + a compensating index survive, foreign entity types are dropped). Runtime navigation flows through `INavigationPopulator` batch loading; consistency across sources is the outbox's job.
- **Transactions are per-source, best-effort sequential** — there is no two-phase commit. The outbox pattern is the cross-source consistency mechanism.
- **Design time**: `DesignTimeDbContextHelper.CreateSqlServer(args, options => ...)` builds a per-source context for `dotnet ef ... -- --datasource <Name>`, enabling one migrations project per database.

**SaveChanges flow**: stamp audit fields → capture domain events from aggregates → serialize to `OutboxMessage` entries → `base.SaveChangesAsync()` (data + outbox in same transaction) → dispatch events in-process → mark outbox processed.

### Outbox Pattern

`OutboxMessage` entries are persisted atomically with aggregate changes, in the **same database as the aggregate** (every relational physical source has its own `OutboxMessages` table). `OutboxProcessor` (background service) drains the outbox of every relational source the host uses — a host never races for another service's outbox rows. Wakes on signal (new entries written) or a **smart wait**: when a cycle sees pending-but-not-yet-eligible rows it sleeps only until the earliest becomes eligible (messages are eligible `Outbox:ProcessingDelaySeconds` after creation, default 5s), otherwise it sleeps the full fallback interval (`Outbox:PollingIntervalSeconds`, default 2s — deployed environments set it high, e.g. 300s, to cut idle polling without adding latency). Processes in batches of 50, retries up to 5 times; a full batch that made progress re-polls immediately. Failed-message retries pace at the polling interval. Integration events published via `IEventBus` target the source named by `Outbox:DataSource` + `Outbox:DatabaseName` (default: SQL Server / Default). Provides at-least-once delivery guarantee with OpenTelemetry metrics for dead-letter tracking. The poll query runs inside an `OutboxPoll` activity that `OutboxPollFilterProcessor` (Aspire package) suppresses from telemetry export.

### Microservices Extraction Seams

The framework is designed so a module can be lifted out of the monolith into its own service without rewriting application code. The invariant: **application/domain code talks to abstractions; transport choices live at the edges.**

- **Message bus** — `IMessageBus` is defined in `MMCA.Common.Application` (`Messaging/`). Infrastructure supplies two implementations: `InProcessMessageBus` (in-monolith) and `BrokerMessageBus` (RabbitMQ via MassTransit, with `IntegrationEventConsumer`). `MessageBusSettings` selects the mode. **`Application`, `Domain`, and `Shared` must never reference `MassTransit` directly** — `MicroserviceExtractionTests` enforces this; depend on `IMessageBus` instead.
- **gRPC transport** (`MMCA.Common.Grpc`) — `AddGrpcServiceDefaults()` registers server-side defaults (`GrpcResultExceptionInterceptor` maps `Result` failures → `RpcException`, plus reflection and compression). `AddTypedGrpcClient<TClient>(serviceName)` wires a generated gRPC client to Aspire service discovery over **HTTP/2 cleartext (h2c)** with a `JwtForwardingClientInterceptor` and the standard Polly pipeline. Note the deliberate `SocketsHttpHandler` override and h2c rationale documented in `DependencyInjection.cs` — target services must serve HTTP/2 on their cleartext endpoint.
- **Cross-service auth (JWKS)** — `IJwksProvider` (Infrastructure, `RsaJwksProvider`) exposes signing keys; `JwksEndpointExtensions` in API serves `/.well-known/jwks.json` so extracted services validate tokens against the issuer's public keys. JWKS discovery is routed through the gateway.
- **Aspire hosting** (`MMCA.Common.Aspire.Hosting`) — AppHost extension methods to wire the cross-cutting infrastructure for extracted deployments: RabbitMQ broker, JWKS service discovery, and gRPC project references.
- **`.Contracts` convention** — any project whose name ends in `.Contracts` automatically pulls in `Grpc.Tools`/`Google.Protobuf` and compiles every `Protos/**/*.proto` with `GrpcServices="Both"` (server + client stubs), so a shared contract package serves both producer and consumer. Configured in `Directory.Build.props`.

### Push Notifications

`MMCA.Common.Infrastructure` ships a SignalR-based push pipeline: `NotificationHub`, `SignalRPushNotificationSender` (real-time delivery), and `NullPushNotificationSender` (no-op fallback). Notification identifier type aliases live in `Source/Core/MMCA.Common.Shared/GlobalUsings.NotificationIdentifierType.cs` and are linked into every `MMCA.Common.*` project via `Directory.Build.props` (same mechanism as `UserIdentifierType`).

### Idempotency

`[Idempotent]` attribute on controller actions. Client provides `Idempotency-Key` header; first response cached 24 hours. Duplicate requests return cached response with `X-Idempotent-Replay: true`. Uses per-key `SemaphoreSlim` for double-check locking.

### Aspire Package

`AddServiceDefaults()` configures OpenTelemetry (logging, metrics, tracing), service discovery, and Polly resilience handlers (30s attempt timeout, 60s circuit breaker window, 90s total timeout). `MapDefaultEndpoints()` adds `/health` (readiness) and `/alive` (liveness) endpoints. The tracing pipeline registers `OutboxPollFilterProcessor`, which drops recurring outbox poll spans (and their SqlClient children from the Azure Monitor distro) from export so idle polling does not dominate telemetry ingestion cost.

### Testing Package

`IntegrationTestBase<TFixture>` provides HTTP client setup, bearer token management, typed `GetAsync<T>`/`PostAsync<T>`/`PutAsync<T>`/`DeleteAsync` helpers, per-test database reset, and thread-safe ID generation. `JwtTokenGenerator` creates test JWT tokens with configurable claims.

## Extension Types (C# Preview)

This project uses C# extension types (`extension(T)` syntax) — requires `LangVersion: preview`. DI registration classes (`DependencyInjection.cs` in each project) use this feature to add methods directly to `IServiceCollection`, `WebApplication`, `ValidationResult`, etc.

## Code Style

The `.editorconfig` enforces strict rules at **error** severity with 5 analyzers (Meziantou, SonarAnalyzer, StyleCop, Roslynator, Microsoft.VisualStudio.Threading). Key conventions:

- File-scoped namespaces (error)
- Braces always required (error)
- `var` only when type is apparent; explicit types for built-ins and non-obvious types (error)
- Private fields: `_camelCase`; constants/static readonly: `PascalCase`
- Expression-bodied members preferred (error)
- `readonly` fields required where possible (error)
- All accessibility modifiers required (error)
- No `this.` qualification (error)
- `TreatWarningsAsErrors` is enabled globally

## Testing

- **Framework:** xUnit v3 + AwesomeAssertions + Moq + coverlet
- **Test runner:** Microsoft Testing Platform (configured in `global.json`)
- **Architecture tests:** NetArchTest.eNhancedEdition (`Tests/Architecture`) — verifies layer/purity/extraction rules at the assembly level
- **E2E:** `MMCA.Common.Testing.E2E` is a *shipped* Playwright fixture package (browser fixtures, Blazor nav helpers, Identity page objects), consumed by downstream apps — not a test project in this solution. The Login/Register/Profile workflow bases assert **WCAG 2.1 AA** via axe-core (`Page.AssertNoAccessibilityViolationsAsync()`), and `PlaywrightFixture` selects the engine from `E2E_BROWSER` (`chromium`/`firefox`/`webkit`, default `chromium`) so consumers can run a cross-browser matrix.
- Test projects mirror Source structure under `Tests/`
- Test files relax naming rules (underscores in method names allowed) and complexity metrics via `.editorconfig` `[Tests/**/*.cs]` section

## Repository Governance Docs & Commit Convention

These docs live **in this repo** (committable, unlike the workspace-level `ArchitecturalAnalysis.md`, `ArchitectureRemediation.md`, etc. described in the parent `CLAUDE.md`):

- `ADRs/` — the accepted architecture decision records (001–011) + index, explaining *why* the core cross-cutting patterns exist (read the relevant one before changing a pattern it describes). These are the **version-controlled canonical** copies (R23 §34); the workspace-root `ADRs/` is now a convenience mirror — add new ADRs here.
- `ArchitectureScorecard.md` — the filled 34-category architecture evaluation (health index, per-category score + evidence). Category numbers (`#1`–`#34`) are referenced as `§NN` in commits.
- `RemediationBacklog.md` — the dated, wave-by-wave remediation log derived from the scorecard; tracks what's done vs. remaining per category.
- `VERSIONING.md` — SemVer + breaking-change policy; the thirteen packages release in lockstep, versions come from MinVer git tags (`vX.Y.Z`), consumers are swept in one pass (no phased rollout).
- `CHANGELOG.md`, `SECURITY.md` — release notes (behavior changes called out) and the security model / consumer responsibilities.

**Commit-message convention:** much of the recent history is remediation work tagged `R<n> §<m>: <summary>` (e.g. `R16 §30: ...`). `R<n>` is the remediation item in the workspace `ArchitectureRemediation.md`; `§<m>` is the scorecard category above. When continuing remediation work, match this format and update `RemediationBacklog.md`.

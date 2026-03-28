# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

MMCA.Common is a .NET 10.0 NuGet package framework for building modular monolith applications using DDD, Clean Architecture, and CQRS patterns. It publishes eight NuGet packages to GitHub Packages — it is not a runnable app itself.

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

# Pack NuGet packages
dotnet pack MMCA.Common.slnx -c Release -o ./nupkgs/
```

Versioning uses MinVer (derived from git tags). CI requires `fetch-depth: 0` for full git history. Release workflow triggers on `v*` tags, extracts version, packs, and pushes to GitHub Packages.

Central package management is enabled — all package versions live in `Directory.Packages.props`. When adding or updating a NuGet package, update the version there (not in individual `.csproj` files).

CI runs on **Ubuntu** — file paths are case-sensitive. Match casing exactly in file/folder references. Every test project must contain at least one test or Microsoft Testing Platform will fail the build (exit code 8).

## Source Layout

```
Source/
├── Core/
│   ├── MMCA.Common.Shared           # Result pattern, errors, DTOs, value objects
│   ├── MMCA.Common.Domain           # Entities, aggregates, domain events, specifications
│   ├── MMCA.Common.Application      # CQRS handlers, decorators, module system, validation
│   └── MMCA.Common.Infrastructure   # EF Core, repositories, UoW, caching, outbox, JWT
├── Presentation/
│   ├── MMCA.Common.API              # Controllers, middleware, idempotency, error mapping
│   └── MMCA.Common.UI               # Blazor components, MudBlazor theme, HTTP resilience
└── Hosting/
    ├── MMCA.Common.Aspire           # Service defaults, OpenTelemetry, health checks
    └── MMCA.Common.Testing          # Integration test base, JWT generator, fixtures

Tests/                               # Mirrors Source/ structure
├── Core/    (Shared.Tests, Domain.Tests, Application.Tests, Infrastructure.Tests)
└── Presentation/    (API.Tests, UI.Tests)
```

## Architecture

Strict layered dependency flow — each layer only references layers below it:

```
API / UI         (presentation)
     ↓
Infrastructure   (EF Core, caching, JWT, outbox)
     ↓
Application      (CQRS handlers, decorators, module system)
     ↓
Domain           (entities, aggregates, domain events, specifications)
     ↓
Shared           (Result pattern, errors, DTOs, value objects)
```

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

A shared `global using UserIdentifierType = int;` in `Source/Core/MMCA.Common.Domain/GlobalUsings.IdentifierType.cs` is linked into all MMCA.Common projects via `Directory.Build.props`.

### Multi-Database Strategy

`DbContextFactory` (scoped) routes repositories to the correct `ApplicationDbContext` subclass based on `DataSource` enum (`CosmosDB`, `SQLite`, `SQLServer`). Per-scope caching ensures repositories in the same request share the same change tracker. Transactions coordinate across SQL Server + SQLite (Cosmos has no multi-document transactions).

**SaveChanges flow**: stamp audit fields → capture domain events from aggregates → serialize to `OutboxMessage` entries → `base.SaveChangesAsync()` (data + outbox in same transaction) → dispatch events in-process → mark outbox processed.

### Outbox Pattern

`OutboxMessage` entries are persisted atomically with aggregate changes. `OutboxProcessor` (background service) polls every 10 seconds for unprocessed messages, processes in batches of 50, retries up to 5 times. Provides at-least-once delivery guarantee with OpenTelemetry metrics for dead-letter tracking.

### Idempotency

`[Idempotent]` attribute on controller actions. Client provides `Idempotency-Key` header; first response cached 24 hours. Duplicate requests return cached response with `X-Idempotent-Replay: true`. Uses per-key `SemaphoreSlim` for double-check locking.

### Aspire Package

`AddServiceDefaults()` configures OpenTelemetry (logging, metrics, tracing), service discovery, and Polly resilience handlers (30s attempt timeout, 60s circuit breaker window, 90s total timeout). `MapDefaultEndpoints()` adds `/health` (readiness) and `/alive` (liveness) endpoints.

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
- Test projects mirror Source structure under `Tests/`
- Test files relax naming rules (underscores in method names allowed) and complexity metrics via `.editorconfig` `[Tests/**/*.cs]` section

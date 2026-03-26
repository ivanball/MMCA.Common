# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

MMCA.Common is a .NET 10.0 NuGet package framework for building modular monolith applications using DDD, Clean Architecture, and CQRS patterns. It is a shared library consumed by downstream applications — not a runnable app itself.

## Build & Test Commands

```bash
# Build
dotnet build MMCA.Common.slnx -c Release

# Test (all projects — uses Microsoft Testing Platform via global.json)
dotnet test --solution MMCA.Common.slnx -c Release

# Test a single project
dotnet test --project Tests/MMCA.Common.API.Tests

# Test a specific test class or method
dotnet test --project Tests/MMCA.Common.API.Tests -- -method "*IdempotencyFilterTests*"

# Pack NuGet packages
dotnet pack MMCA.Common.slnx -c Release -o ./nupkgs/
```

Versioning uses MinVer (derived from git tags). CI requires `fetch-depth: 0` for full git history.

Central package management is enabled — all package versions live in `Directory.Packages.props`. When adding or updating a NuGet package, update the version there (not in individual `.csproj` files).

CI runs on **Ubuntu** — file paths are case-sensitive. Match casing exactly in file/folder references.

## Architecture

Strict layered dependency flow — each layer only references layers below it:

```
API / UI.Shared  (presentation)
       ↓
Infrastructure   (EF Core, caching, JWT, outbox)
       ↓
Application      (CQRS handlers, decorators, module system)
       ↓
Domain           (entities, aggregates, domain events, specifications)
       ↓
Shared           (Result pattern, errors, DTOs, value objects)
```

### Key Patterns

- **Result pattern** — `Result<T>` with `Error`/`ErrorType` instead of exceptions for flow control. `ApiControllerBase.HandleFailure()` maps `ErrorType` to HTTP status codes via `FrozenDictionary`.
- **CQRS** — `ICommandHandler<TCmd, TResult>` and `IQueryHandler<TQuery, TResult>` with decorator pipeline (Logging → Caching → Transactional → Handler). `AddApplicationDecorators()` must be called **after** all modules register their handlers, since Scrutor's `TryDecorate` requires existing registrations.
- **DDD** — `BaseEntity<TId>`, `AuditableAggregateRootEntity`, domain events, invariants, specifications.
- **Module system** — Feature-based isolation via Scrutor convention scanning and `ModulesSettings`. Downstream modules register services via `ScanModuleApplicationServices<TAssemblyMarker>()` where `TAssemblyMarker` is typically a `ClassReference` type in the module's assembly.
- **Repository + UoW** — `EFRepository<TEntity, TId>` with `UnitOfWork` pattern.
- **Multi-DB** — Abstract DbContext strategy supporting Cosmos DB, SQLite, and SQL Server.
- **Outbox pattern** — Reliable domain event publishing.
- **Idempotency** — Request deduplication via `[Idempotent]` attribute on controller actions.

### Entity Identifier Convention

A shared global using alias in `Source/MMCA.Common.Domain/GlobalUsings.IdentifierType.cs` (e.g., `global using UserIdentifierType = int;`) is linked into all MMCA.Common projects via `Directory.Build.props`. This provides a single place to change identifier types across the framework.

### Extension Types (C# Preview)

This project uses C# extension types (`extension(T)` syntax) — requires `LangVersion: preview`. DI registration classes like `DependencyInjection.cs` use this feature.

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

- **Framework:** xUnit v3 + FluentAssertions + Moq + coverlet
- **Test runner:** Microsoft Testing Platform (configured in `global.json`)
- Test projects mirror Source structure under `Tests/`
- No UI.Shared test project exists

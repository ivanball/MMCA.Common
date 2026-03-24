# MMCA — Modular Monolith Clean Architecture

A .NET 10.0 framework for building modular monolith applications using DDD, Clean Architecture, and CQRS patterns.

## Packages

| Package | Description |
|---|---|
| `MMCA.Common.Shared` | Result pattern, value objects, error handling, DTOs |
| `MMCA.Common.Domain` | DDD base entities, aggregate roots, domain events, specifications |
| `MMCA.Common.Application` | CQRS handlers, decorator pipeline, module system, query service |
| `MMCA.Common.Infrastructure` | EF Core multi-DB, repositories, UoW, caching, JWT, outbox |
| `MMCA.Common.API` | Base controllers, middleware, idempotency, error-to-HTTP mapping |
| `MMCA.UI.Shared` | Blazor shared components, auth state, MudBlazor theme |

## Requirements

- .NET 10.0 with `LangVersion: preview` (uses C# extension types)

## Quick Start

```bash
dotnet add package MMCA.Common.API
```

Each package transitively includes its dependencies (`API → Infrastructure → Application → Domain → Shared`).

## License

MIT

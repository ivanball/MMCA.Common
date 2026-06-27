# MMCA: Modular Monolith Clean Architecture

A .NET 10.0 framework for building modular monolith applications using DDD, Clean Architecture, and CQRS patterns.

## Packages

| Package | Description |
|---|---|
| `MMCA.Common.Shared` | Result pattern, value objects, error handling, DTOs |
| `MMCA.Common.Domain` | DDD base entities, aggregate roots, domain events, specifications |
| `MMCA.Common.Application` | CQRS handlers, decorator pipeline, module system, query service, `IMessageBus` |
| `MMCA.Common.Infrastructure` | EF Core multi-DB, repositories, UoW, caching, JWT, JWKS, outbox, message bus, SignalR |
| `MMCA.Common.API` | Base controllers, middleware, idempotency, error-to-HTTP mapping, JWKS endpoint |
| `MMCA.Common.Grpc` | gRPC server defaults, Result→RpcException mapping, JWT-forwarding client interceptor, typed gRPC clients |
| `MMCA.Common.UI` | Blazor shared components, auth state, MudBlazor theme |
| `MMCA.Common.Aspire` | Service defaults, OpenTelemetry, health checks, Polly resilience |
| `MMCA.Common.Aspire.Hosting` | Aspire AppHost extensions: RabbitMQ broker, JWKS service discovery, gRPC project wiring |
| `MMCA.Common.Testing` | Integration test base, JWT generator, fixtures |
| `MMCA.Common.Testing.E2E` | Playwright E2E infrastructure: browser fixtures, Blazor nav helpers, Identity page objects |
| `MMCA.Common.Testing.UI` | bUnit component-test base, MudBlazor provider harness, interaction helpers |
| `MMCA.Common.Testing.Architecture` | `IArchitectureMap` + reusable NetArchTest rule library + abstract test bases (consumed by each repo's `*.Architecture.Tests`) |

## Requirements

- .NET 10.0 with `LangVersion: preview` (uses C# extension types)

## Quick Start

```bash
dotnet add package MMCA.Common.API
```

Each package transitively includes its dependencies (`API → Infrastructure → Application → Domain → Shared`).

## Building a new app on MMCA.Common

New to the framework? **[GETTING-STARTED.md](GETTING-STARTED.md)** is the step-by-step guide for standing up a brand-new application: solution plumbing, a module vertical slice (domain → handler → endpoint → migration), the Aspire host, the architecture-fitness map, and a fully worked extraction of a module into its own microservice. It builds monolith-first, then shows the "extract later, without a rewrite" path.

## Architecture & docs

- **[Architecture scorecard](ArchitectureScorecard.md)**: the framework graded against a 34-category review rubric, with every category score, the weighted health index, top strengths, and the honest gaps.
- **[Architecture Decision Records](ADRs/README.md)**: the accepted ADRs (001-022) explaining *why* the core cross-cutting patterns exist (outbox dual-dispatch, database-per-service, auth dual-fetch, soft-delete vs. erasure, gRPC extraction, and more). See `ADRs/README.md` for the current count/range.
- **[Contributor guide](CLAUDE.md)**: package layout, layer dependency rules, and how to extend the framework.

## License

MIT

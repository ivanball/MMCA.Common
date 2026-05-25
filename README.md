# MMCA — Modular Monolith Clean Architecture

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

## Requirements

- .NET 10.0 with `LangVersion: preview` (uses C# extension types)

## Quick Start

```bash
dotnet add package MMCA.Common.API
```

Each package transitively includes its dependencies (`API → Infrastructure → Application → Domain → Shared`).

## License

MIT

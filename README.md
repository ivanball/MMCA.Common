# MMCA: Modular Monolith Clean Architecture

[![NuGet](https://img.shields.io/nuget/v/MMCA.Common.API.svg?label=nuget)](https://www.nuget.org/packages/MMCA.Common.API)
[![Downloads](https://img.shields.io/nuget/dt/MMCA.Common.API.svg)](https://www.nuget.org/packages/MMCA.Common.API)
[![CI](https://github.com/ivanball/MMCA.Common/actions/workflows/ci.yml/badge.svg)](https://github.com/ivanball/MMCA.Common/actions/workflows/ci.yml)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](https://github.com/ivanball/MMCA.Common/blob/main/LICENSE)

A .NET 10 framework for building a modular monolith you can extract services out of later, without a rewrite.

## Why it exists

Most teams face the same fork: start as a monolith and pay for it when you need to scale a piece of it, or start with microservices and pay for it immediately. MMCA takes the first path and removes the later cost. Modules are discovered and registered in dependency order, each owns its own database and transactional outbox from day one, and application code talks to abstractions (`IMessageBus`, `INavigationPopulator`, repositories) while transport choices stay at the edges. Lifting a module into its own gRPC service changes hosting and configuration, not your handlers.

What comes in the box:

- **DDD + CQRS** with rich aggregates, static factories returning `Result<T>`, and a Scrutor decorator pipeline (feature gate, logging, caching, validation, transactions) that wraps every handler without touching one.
- **The Result railway** instead of exceptions for expected failures, mapped consistently to HTTP Problem Details (RFC 9457) and gRPC status.
- **Transactional outbox** persisted atomically with your data, drained per data source, at-least-once, with OpenTelemetry metrics.
- **Database-per-module** over one context class per engine, with cross-source relationships degrading automatically instead of breaking.
- **Auth at the edge**: JWKS cross-service validation with no shared secret, permission-based authorization, rotating refresh tokens, idempotency keys, and rate limiting.
- **Architecture that fails the build**: a compile-time layer guard plus a shared NetArchTest rule library, re-run identically in every consuming repo (counts in [FACTS.md](https://github.com/ivanball/MMCA.Common/blob/main/FACTS.md)).

The framework is not a demo. It runs two production applications (a conference platform and an e-commerce store) plus a minimal reference app, and it is graded against a published 34-category rubric with the gaps left visible.

## Install

```bash
dotnet add package MMCA.Common.API
```

Each package transitively includes the layers below it (`API -> Infrastructure -> Application -> Domain -> Shared`), so one reference is usually enough to start. Requires **.NET 10** with `LangVersion: preview` (the DI registration uses C# extension types).

## Try it in 15 minutes

Scaffold a complete solution on the framework, no cloning and no credentials:

```bash
dotnet new install MMCA.Templates
dotnet new mmca-app -n Contoso.Support --module Orders --aggregate Order
```

That is a warning-free build under all five analyzers and a passing test run including the architecture-fitness rules, with no database needed. [**Getting Started**](https://ivanball.github.io/docs/guides/common-GETTING-STARTED.html) takes it from there to a running, migrated app in six steps.

[**MMCA.Helpdesk**](https://github.com/ivanball/MMCA.Helpdesk) is the runnable seed the template is generated from: one `Tickets` module exercised end to end through all five layers, monolith-first, with the extraction path already in place. Clone it, run `dotnet run --project Source/Hosting/MMCA.Helpdesk.AppHost`, and read [Building by Hand](https://ivanball.github.io/docs/guides/common-BUILD-BY-HAND.html) alongside it: every phase of that walkthrough maps to real code in that repo.

## Documentation

The full reference library is published at **<https://ivanball.github.io/docs/>**:

- **[Architecture Decision Records](https://ivanball.github.io/docs/adr/)**: the context, decision, and trade-offs behind every cross-cutting pattern. That index owns the canonical count and range.
- **[Getting Started](https://ivanball.github.io/docs/guides/common-GETTING-STARTED.html)**: standing up a new application from the `MMCA.Templates` scaffold, in six steps. [Templates](https://ivanball.github.io/docs/guides/common-TEMPLATES.html) documents every parameter of the four templates.
- **[Building by Hand](https://ivanball.github.io/docs/guides/common-BUILD-BY-HAND.html)**: the same solution phase by phase (build plumbing, a module vertical slice, the Aspire host, the fitness map, and a worked service extraction), for retrofitting an existing solution or understanding what the scaffold generated.
- **[Onboarding guide](https://ivanball.github.io/docs/onboarding/)**: a chapter-per-subsystem walkthrough of every first-party type.
- **[Architecture scorecard](https://ivanball.github.io/docs/governance/common-ArchitectureScorecard.html)**: the framework graded against the 34-category rubric, with every score citing the code that earns it.
- **[Article series](https://ivanball.github.io/writing.html)**: long-form deep dives on the patterns above, each one grounded in this source.

## Packages

Every package ships at the same version and is bumped in lockstep (ADR-016). The authoritative list lives in [FACTS.md](https://github.com/ivanball/MMCA.Common/blob/main/FACTS.md).

| Package | Description |
|---|---|
| `MMCA.Common.Shared` | Result pattern, value objects, error handling, DTOs |
| `MMCA.Common.Domain` | DDD base entities, aggregate roots, domain events, specifications |
| `MMCA.Common.Application` | CQRS handlers, decorator pipeline, module system, query service, `IMessageBus` |
| `MMCA.Common.Infrastructure` | EF Core multi-DB, repositories, UoW, caching, JWT, JWKS, outbox, message bus, SignalR |
| `MMCA.Common.API` | Base controllers, middleware, idempotency, error-to-HTTP mapping, JWKS endpoint |
| `MMCA.Common.Grpc` | gRPC server defaults, Result to RpcException mapping, JWT-forwarding client interceptor, typed gRPC clients |
| `MMCA.Common.UI` | Blazor shared components, auth state, MudBlazor theme |
| `MMCA.Common.UI.Web` | Blazor Web (Server + WASM) UI host helpers built on `MMCA.Common.UI` |
| `MMCA.Common.UI.Maui` | .NET MAUI UI head (the one MAUI-TFM package; built and packed by dedicated windows jobs, outside `MMCA.Common.slnx`, ADR-042) |
| `MMCA.Common.Aspire` | Service defaults, OpenTelemetry, health checks, Polly resilience |
| `MMCA.Common.Aspire.Hosting` | Aspire AppHost extensions: RabbitMQ broker, JWKS service discovery, gRPC project wiring |
| `MMCA.Common.Testing` | Integration test base, JWT generator, fixtures |
| `MMCA.Common.Testing.E2E` | Playwright E2E infrastructure: browser fixtures, Blazor nav helpers, Identity page objects |
| `MMCA.Common.Testing.UI` | bUnit component-test base, MudBlazor provider harness, interaction helpers |
| `MMCA.Common.Testing.Architecture` | `IArchitectureMap` + reusable NetArchTest rule library + abstract test bases (consumed by each repo's `*.Architecture.Tests`) |

Packages are published to **nuget.org** and mirrored to GitHub Packages (ADR-053).

## Built on it

- **[MMCA.Helpdesk](https://github.com/ivanball/MMCA.Helpdesk)**: the minimal reference app and the companion to Getting Started.
- Two production applications (conference platform, e-commerce store) track the framework in lockstep and are the source of the case-study material in the docs.

## Contributing

`main` is protected: branch, open a pull request, let the required checks go green, then squash-merge. See [CONTRIBUTING.md](https://github.com/ivanball/MMCA.Common/blob/main/CONTRIBUTING.md) for the exact checks and [CLAUDE.md](https://github.com/ivanball/MMCA.Common/blob/main/CLAUDE.md) for package layout and layer dependency rules.

## License

Apache License 2.0, see [LICENSE](https://github.com/ivanball/MMCA.Common/blob/main/LICENSE). The license includes an express patent grant from contributors to users.

# ADR-007: Synchronous Cross-Service Calls via gRPC Contracts

## Status
Accepted.

## Context
Once modules became separate service processes, the in-process interface calls between them (e.g.
Conference → Engagement's `IBookmarkCountService`, Engagement → Conference's
`ISessionBookmarkValidationService`) had to cross a process boundary. Asynchronous integration
events (outbox → broker, ADR-003) cover fire-and-forget flows, but some calls need a **synchronous
answer**. We needed a transport for those that preserved the existing application interfaces and the
`Result<T>` error model, without coupling application/domain code to a transport.

## Decision
Use **gRPC**, exposed through `MMCA.Common.Grpc`, with a contract-package convention:

- **`*.Contracts` projects** hold the `.proto` definitions plus a gRPC adapter that implements the
  **same in-process service interface** the modules already used. Any project ending in `.Contracts`
  auto-compiles `Protos/**/*.proto` with both server and client stubs (`Directory.Build.props`).
- **Typed clients** via `AddTypedGrpcClient<T>(serviceName)` resolve `http://<service>` through
  Aspire service discovery, wrapped in the standard Polly resilience pipeline and a
  `JwtForwardingClientInterceptor` (the inbound bearer token is forwarded downstream).
- **`Result` failures over the wire**: the server-side `GrpcResultExceptionInterceptor` maps a failed
  `Result` to an `RpcException` carrying the structured `ErrorType`/code, mirroring the HTTP
  `HandleFailure` edge mapping (ADR-013). Adapters whose interface returns a `Result` (for example
  `SessionBookmarkValidationServiceGrpcAdapter`) re-hydrate that failure **client-side** by parsing the
  `error-{i}-*` trailers, so the caller sees the same `Result` shape it would from an in-process call.
  Adapters whose interface returns a plain type (for example `Task<int>` or `Task<IReadOnlyList<T>>`)
  surface a remote failure as a thrown exception instead.
- **HTTP/2 cleartext (h2c)**: the REST services serve HTTP/2 on their cleartext endpoint so clients
  negotiate without TLS/ALPN (a deliberate `SocketsHttpHandler` override).
- **Federated auth, not a shared secret**: services validate forwarded JWTs against the issuer's
  JWKS (ADR-004), discovered through the gateway.
- **Disabled-module stubs**: when a service runs with a peer module disabled, it registers a
  `Disabled*` stub for that peer's interface, so resolution always succeeds.

## Rationale
- **No business-logic rewrite** — the gRPC adapter implements the interface modules already depend
  on; swapping in-process for cross-process is a registration change.
- **Transport stays at the edge** — `MicroserviceExtractionTests` forbid `MassTransit`/gRPC types in
  Application/Domain/Shared, so the choice is reversible and the core stays clean.
- **Strong contracts** — the `.proto` definitions plus the in-process interface the adapter
  implements are the wire surface. A `[ServiceContract(version)]` attribute (MMCA.Common.Shared) is
  provided to mark and version contract types explicitly, but it is an **available seam, not yet
  applied** — no contract type carries it today, and no fitness rule enforces it; the wire surface is
  currently defined by the `.proto` files alone.

## Trade-offs
- **Bidirectional pairs need care.** Conference ↔ Engagement is a mutual gRPC pair; the AppHost
  deliberately omits a reciprocal `WaitFor` to avoid a startup deadlock — transient "peer not ready"
  errors self-heal via the resilience pipeline.
- **h2c assumptions.** Target services must serve HTTP/2 on cleartext; the Notification service runs
  `Http1AndHttp2` for its SignalR WebSocket upgrade, unlike the `Http2`-only REST services.
- **Operational surface.** gRPC adds proto tooling, service discovery, and resilience tuning to the
  deployment.

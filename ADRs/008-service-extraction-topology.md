# ADR-008: Extraction of the Modular Monolith into Per-Module Services + Gateway

## Status
Accepted.

## Context
ADC began as a modular monolith: one `MMCA.ADC.WebAPI` host loaded every module (Identity, Conference,
Engagement, Notification) in-process via the `ModuleLoader`, sharing one database and dispatching
domain/integration events in-process. The modular structure — per-module Domain/Application/
Infrastructure/API/Shared, with strict module isolation enforced by the architecture tests — was always
maintained, but everything deployed and scaled as a single unit.

Two forces pushed past that:

- **Uneven scale + cost.** Traffic is lopsided — the public Conference read paths spike around the event
  while Identity/Engagement/Notification stay flat (the 2026 conference peaked at ~67 concurrent, almost
  all on the Conference surface). Scaling one monolith means scaling everything.
- **Fault & deploy isolation.** A bad deploy or a runaway path in one module took the others down with
  it, and every change redeployed the whole surface.

The modular boundaries were already clean enough to split along; the question was how to extract
**without rewriting business logic** or losing the ability to reason about — and revert — the system.

## Decision
Extract **one service host per module** — `MMCA.ADC.{Identity,Conference,Engagement,Notification}.Service`
— and front them with a single **YARP reverse-proxy Gateway** (`MMCA.ADC.Gateway`, pinned to
`https://localhost:6001`). Delete the combined `MMCA.ADC.WebAPI` host.

- **Each service is the monolith with one module enabled.** The hosts still run `ModuleLoader`, just with
  `Modules:{Module}:Enabled=true` for their own module; disabled peers are satisfied by `Disabled*` stubs.
  The Domain/Application/Shared code is identical whether it runs in-process or extracted.
- **The Gateway is the only client entry point.** It owns the route→service map (`/Auth`, `/Events`,
  `/Bookmarks`, `/hubs`, `/.well-known`, …); clients (Blazor/MAUI) never address a service directly. It
  has no DbContext or controllers: security-headers middleware (ADR-023), CORS, static files, a
  `/privacy` minimal-API endpoint, and the route forwarders (ADC; Store's Gateway is the same minus the
  static files).
- **Cross-service communication uses edge transports:** synchronous calls over gRPC contracts (ADR-007);
  asynchronous flows over the outbox → MassTransit broker (ADR-003, ADR-006). Token validation is
  federated via JWKS through the Gateway (ADR-004). Each service owns its own database (ADR-006).
- **Transport stays at the edge, enforced.** `MicroserviceExtractionTests` in the architecture suite
  forbid gRPC / MassTransit / Protobuf dependencies in any Domain, Application, or Shared assembly, so the
  core stays host-agnostic and the split stays reversible.

## Rationale
- **No business-logic rewrite.** Because a service is just the monolith with one module enabled,
  extraction was a hosting/wiring change, not a domain change — and the module-isolation tests already
  guaranteed modules didn't reach into each other's internals.
- **One entry point.** A single Gateway keeps client config trivial (one pinned URL the MAUI app bakes
  in), centralizes CORS and auth-forwarding, and lets the internal services run cleartext h2c without
  exposing that to clients.
- **Per-module split (not coarser or finer).** The module already was the consistency and ownership
  boundary; one service per module maps deploy/scale units onto boundaries the team already reasons about.
- **Reversible by construction.** Transport at the edge + the `ModuleLoader` mean a service can be
  re-collapsed into a combined host (or peers co-hosted) by changing configuration, not code — useful
  insurance for a small team adopting microservices.

## Trade-offs
- **Operational complexity.** Four deployables plus a Gateway, service discovery, a broker, and
  per-service databases — versus one process. Mitigated locally by Aspire orchestration and in prod by
  Bicep / Azure Container Apps.
- **Distributed-systems semantics.** Cross-service consistency is eventual (outbox + integration events);
  there are no cross-service transactions or cross-database FKs (ADR-006). Bidirectional gRPC pairs
  (Conference ↔ Engagement) need deliberate startup handling (ADR-007).
- **Transport constraints leak into hosting.** The REST services run `Http2`-only on cleartext for h2c
  gRPC, while Notification runs `Http1AndHttp2` for its SignalR WebSocket upgrade — a per-host Kestrel
  nuance that didn't exist in the monolith.
- **Duplicated host wiring.** Each service repeats the same pipeline setup; shared concerns live in
  `MMCA.Common.API` / `ServiceDefaults` to limit the drift.

## Applicability
This ADR is framed around ADC (the first repo extracted), but the same topology is now the framework's
**standard extraction shape**, not an ADC-only choice. MMCA.Store followed it: one service host per
module (`MMCA.Store.{Catalog,Identity,Sales}.Service`) behind a single `MMCA.Store.Gateway` (also
pinned to `https://localhost:6001`), with its combined `MMCA.Store.WebAPI` host likewise deleted.
Store's cross-service topology differs in transport detail (see ADR-012 — both apps now run Profile A),
but the extraction shape is identical.

## Related
ADR-003 (outbox dual dispatch), ADR-004 (cross-service token validation via JWKS), ADR-006 (database
per service), and ADR-007 (gRPC cross-service calls) are the facet decisions that sit under this extraction.

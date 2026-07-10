# ADR-012: gRPC-Host Transport Convention (Http2-only h2c vs. Http1AndHttp2 + ALPN)

## Status
Accepted.

## Update (2026-06-22): Store converged to Profile A
Store originally chose Profile B, but its cross-service gRPC failed in Azure Container Apps. With
`Http1AndHttp2` Kestrel + `transport: 'auto'` ingress on a **cleartext** endpoint there is no ALPN, so
envoy delivered HTTP/1.1 to Catalog and Identity (which **do** serve inbound gRPC: Sales → Catalog,
Sales → Identity) and Kestrel rejected it with `HTTP_1_1_REQUIRED` (Sales `AddItemCommand` 500'd calling
`IProductVariantService.ExistsAsync`). The lesson: a "consumer-only / one-directional" edge topology
still has inbound **cleartext gRPC servers**, and ACA cleartext ingress cannot deliver HTTP/2 to them
under Profile B.

Fixed in commit 49b7283 (deployed green) by adopting **Profile A** for Store:
- Catalog + Identity run `Http2`-only on cleartext (`Kestrel:EndpointDefaults:Protocols=Http2`), ACA
  ingress `transport: 'http2'`, with **TCP** startup/liveness probes (Http2-only Kestrel rejects the
  kubelet's HTTP/1.1 `httpGet` probes with GOAWAY).
- Gateway forwards HTTP/2 (`ForwardHttp2=true`, `VersionPolicy=RequestVersionExact`); Catalog/Identity
  routes carry HTTP/2, Sales routes stay HTTP/1.1.
- Sales (no gRPC server) stays `Http1AndHttp2` with `transport: 'http'`.
- JWKS authority differs by environment: **prod ACA** keeps the direct `http://identity` authority (the
  http2 ingress carries the HTTP/1.1 JwtBearer JWKS metadata fetch to the container), while the
  **local-Aspire** path was subsequently moved to the gateway-routed `WithJwksDiscovery(identity, gateway)`
  form (the D32 fix) — a single-arg local `WithJwksDiscovery` would aim the HTTP/1.1 backchannel at the
  now-Http2-only Identity HTTPS endpoint and fail on the ALPN mismatch. So Store's local JWKS now matches
  Profile A's gateway-routed discovery; only prod uses the in-cluster direct authority.

**Both consumers now use Profile A** for their gRPC-serving edges. `Http1AndHttp2` (Profile-B Kestrel)
survives on two non-gRPC-serving hosts for two different reasons: ADC's **Notification** service (its
WebSocket Upgrade handshake needs HTTP/1.1) and Store's **Sales** service (a consumer-only host that
serves no inbound cleartext gRPC, so it never needed Http2-only). Profile B is retained below as (a) the
original rationale for the split and (b) the still-valid rule for those no-inbound-gRPC hosts.
*(Since 2026-07-09, ADC's Notification does serve one inbound gRPC edge, from a dedicated Http2-only
endpoint alongside its Http1AndHttp2 default endpoint: see the mixed-endpoint update below.)*

## Update (2026-07-09): ADC Notification adds a mixed-endpoint profile (per-endpoint protocols)

The live-channel push seam (ADR-039) gave ADC's Notification service an **inbound cleartext gRPC
server** (`LiveChannelPushService.PushToChannel`, called best-effort by Engagement command handlers)
while it still hosts the SignalR hub whose WebSocket transport needs the HTTP/1.1 Upgrade handshake.
That combination breaks the original per-host rule ("a WebSocket host cannot be a cleartext gRPC
server"): neither whole-host profile fits, because the constraint was only ever per **endpoint**, not
per host. The resolution is to split protocols across two Kestrel endpoints in one process:

- **Kestrel (per-endpoint, not per-host):** the default `http` endpoint (container port 8080) stays
  `Http1AndHttp2` for REST, health probes, and the SignalR WebSocket Upgrade; a second named `grpc`
  endpoint (container port 8081) is `Http2`-only for h2c prior-knowledge gRPC. Declared in
  `Kestrel:Endpoints` in the service's appsettings (with distinct fixed ports in the dev profile).
- **Aspire (local):** the AppHost wires `engagementService.WithReference(notificationService)`, which
  injects `services__notification__grpc__0`; the typed client registers against the **named endpoint
  scheme** `http://_grpc.notification` so service discovery resolves the gRPC port, not the default
  one. Deliberately no `WaitFor`: the publish path is best-effort and must not couple Engagement
  startup to Notification availability.
- **ACA (prod):** the main ingress stays HTTP/1.1-capable for WebSockets; the gRPC port is exposed via
  `additionalPortMappings` as a dedicated **internal TCP** port mapping (TCP passthrough sidesteps the
  envoy single-transport limitation: one app cannot serve HTTP/1.1 WebSockets and end-to-end HTTP/2 on
  the same HTTP ingress). The Bicep injects the same discovery variable pointing at
  `http://<app>-notification:8081`.
- **Probes and gateway: unchanged.** The default endpoint still answers the kubelet's HTTP/1.1
  `httpGet` probes (no TCP-probe workaround needed, unlike full Profile A hosts), and the gateway
  never routes the gRPC endpoint (it is service-to-service only).

Rule refinement: a service that needs the HTTP/1.1 Upgrade handshake AND must serve inbound cleartext
gRPC uses this **mixed-endpoint profile**: Profile B protocols on the default endpoint, a Profile A
`Http2`-only named endpoint for gRPC, discovery via the named-endpoint scheme, and (in ACA) an
additional internal TCP port. It costs one extra port everywhere (appsettings, launch profile, Bicep)
and is only worth it when both constraints genuinely meet in one host.

## Context
Once modules were extracted into separate service hosts (ADR-008) that call each other synchronously
over gRPC (ADR-007), each service's Kestrel had to serve **both** REST traffic (HTTP/1.1 from the
gateway and clients) and gRPC traffic (HTTP/2 from peer services). On a **cleartext** endpoint there
is no TLS, so there is no ALPN to negotiate the protocol — Kestrel must be told up front which
protocol(s) the cleartext port speaks. Two valid configurations exist, and the two downstream apps
deliberately pick different ones because their cross-service topologies differ:

- **MMCA.ADC** has a **bidirectional** gRPC pair (Conference ↔ Engagement, plus Notification →
  Identity). A gRPC client over h2c must reach a server that speaks HTTP/2 on cleartext.
- **MMCA.Store** was *originally assumed* to have only **one-directional, consumer-only** gRPC edges
  (Sales → Catalog, Sales → Identity). That assumption proved wrong in Azure Container Apps — Catalog
  and Identity **do** serve inbound cleartext gRPC — which is why Store later converged on Profile A
  (see the Update above). This section preserves the original split as historical rationale.

The subtlety: a gRPC client using h2c **prior knowledge** sends an HTTP/2 preface with no upgrade
handshake. If the server's cleartext endpoint is `Http1AndHttp2`, Kestrel — lacking ALPN on
cleartext — answers HTTP/1.1 and the client fails with `HTTP_1_1_REQUIRED`. Forcing `Http2`-only on
cleartext fixes gRPC but then a default `HttpClient` (HTTP/1.1) — e.g. the JwtBearer JWKS backchannel
or the YARP forwarder — can no longer hit that endpoint directly. So the Kestrel choice forces
matching choices for **gateway forwarding** and **JWKS discovery routing**.

## Decision
Pick one of two coherent transport profiles per app, and wire the gateway forwarder and JWKS
discovery to match.

### Profile A — ADC: `Http2`-only h2c + gateway-routed JWKS
Use when services must **serve** gRPC on cleartext (any bidirectional / inbound gRPC edge).

- **Kestrel:** `ConfigureEndpointDefaults(o => o.Protocols = HttpProtocols.Http2)` (also
  `"Kestrel:EndpointDefaults:Protocols": "Http2"` in appsettings). The cleartext endpoint is
  HTTP/2-only (h2c prior knowledge), so peer gRPC clients negotiate without TLS/ALPN.
- **Gateway:** `ForwardHttp2 = true` → YARP forwards REST as HTTP/2 (`HttpVersion.Version20`,
  `VersionPolicy = RequestVersionExact`). `RequestVersionOrLower` would silently downgrade to HTTP/1.1,
  which the Http2-only backend rejects with `HTTP_1_1_REQUIRED`, so the policy must be *exact*. In Azure
  Container Apps, ingress must be `transport: http2`.
- **JWKS discovery:** `WithJwksDiscovery(identity, gateway)`. The default JwtBearer metadata
  backchannel is HTTP/1.1 and **cannot** reach the Http2-only Identity endpoint directly, so the
  authority is set to the **gateway** HTTPS origin; the gateway terminates TLS, speaks HTTP/1.1 + 2
  via ALPN, and routes `/.well-known/*` on to Identity over HTTP/2 (ADR-004).
- **Exception:** the Notification service keeps `Http1AndHttp2` on its default endpoint because
  SignalR's WebSocket transport needs the HTTP/1.1 Upgrade handshake. Since 2026-07-09 it also serves
  one inbound gRPC edge from a dedicated `Http2`-only named endpoint (the mixed-endpoint profile in
  the update above), so "serves no inbound gRPC" no longer holds for the host, only for its default
  endpoint.

### Profile B — `Http1AndHttp2` + HTTPS/ALPN + `ForwardHttp2=false` + direct JWKS (Store's original choice; now retained only as the SignalR/WebSocket exception)
Use when no service needs to **serve** gRPC on cleartext (consumer-only / one-directional gRPC).

- **Kestrel:** `ConfigureEndpointDefaults(o => o.Protocols = HttpProtocols.Http1AndHttp2)` (also
  `"Protocols": "Http1AndHttp2"`). The cleartext endpoint defaults to HTTP/1.1 (no ALPN); the
  **HTTPS** endpoint negotiates HTTP/1.1 **or** HTTP/2 via ALPN. gRPC clients use the HTTPS endpoint
  (the AppHost selects the `https` launch profile) so they get HTTP/2 through ALPN.
- **Gateway:** `ForwardHttp2 = false` (default) → YARP forwards REST as HTTP/1.1, which the
  `Http1AndHttp2` backends accept on cleartext. In ACA, envoy ingress is plain HTTP/1.1.
- **JWKS discovery:** `WithJwksDiscovery(identity)` with **no gateway argument**. The default
  JwtBearer backchannel reaches Identity's HTTPS endpoint and ALPN negotiates HTTP/2, so no gateway
  hop is needed for discovery (the gateway still routes `/.well-known/*` so the canonical issuer
  origin serves the discovery doc for clients).

### When to use which
- **Any service that hosts an inbound gRPC server reachable over cleartext h2c (especially a
  bidirectional pair) → Profile A.** Cleartext h2c prior knowledge requires an `Http2`-only endpoint;
  that in turn forces `ForwardHttp2=true` and gateway-routed JWKS.
- **Only consumer-only / one-directional gRPC, with gRPC riding the HTTPS/ALPN endpoint → Profile B.**
  Keep `Http1AndHttp2`, `ForwardHttp2=false`, and direct `WithJwksDiscovery(identity)`.
- A service needing the **HTTP/1.1 Upgrade** handshake (SignalR WebSockets) must keep `Http1AndHttp2`
  on that endpoint (ADC's Notification service). If it must also serve cleartext gRPC, do not flip the
  host profile: add a dedicated `Http2`-only named endpoint instead (the 2026-07-09 mixed-endpoint
  update above).

## Rationale
- **The Kestrel protocol choice is the root constraint**; the gateway-forward mode and the JWKS
  authority are downstream consequences, not independent knobs. Documenting them as a pair prevents
  the half-configured failure modes (`HTTP_1_1_REQUIRED` on gRPC, or a JwtBearer backchannel that
  can't fetch JWKS from an Http2-only endpoint).
- **Each app picks the minimum that its topology needs.** ADC's bidirectional gRPC forced the
  `Http2`-only profile (and the gateway-routed JWKS that comes with it) from the start. Store
  originally chose Profile B on the assumption its gRPC edges were consumer-only, but Catalog and
  Identity in fact serve inbound cleartext gRPC (Sales → Catalog, Sales → Identity), so it converged
  on Profile A (see the Update above). The only remaining Profile B case is a service that serves no
  inbound cleartext gRPC yet needs the HTTP/1.1 Upgrade handshake — ADC's Notification (SignalR).

## Trade-offs
- **Two profiles to keep straight.** A service that gains an inbound gRPC edge must migrate from
  Profile B to Profile A *and* flip `ForwardHttp2` and the JWKS wiring together, or it breaks.
- **ACA ingress coupling.** Profile A requires `transport: http2` on the container app ingress;
  Profile B uses default HTTP/1.1 ingress. The Bicep must match the chosen profile.
- **Mixed profiles within one app are possible but sharp-edged** (ADC's Notification and Store's Sales
  each run `Http1AndHttp2` Kestrel defaults inside an otherwise-Profile-A app); only do this for a
  service whose default endpoint serves no cleartext gRPC, and document why.
- **Mixed endpoints within one host cost a port.** The Notification mixed-endpoint profile needs the
  extra gRPC port declared consistently in appsettings, the dev launch profile, and the ACA
  `additionalPortMappings`; a missing declaration in any one of them fails only at runtime (discovery
  resolves a port nothing listens on, or ACA never exposes it).

## Related
- ADR-004 (cross-service token validation via JWKS / OIDC discovery), ADR-007 (gRPC cross-service
  calls), ADR-008 (monolith → services + gateway topology), ADR-039 (live-channel push: the seam
  that gave Notification its inbound gRPC edge and motivated the mixed-endpoint profile).

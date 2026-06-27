# Architecture Decision Records

Accepted ADRs explaining *why* the core cross-cutting patterns exist. Read these before changing a
pattern they describe — they capture context and trade-offs that aren't obvious from the code.

| # | Decision | Summary |
|---|----------|---------|
| [001](001-manual-dto-mapping.md) | Manual DTO mapping | Per-entity Mapperly source-generated mappers chosen over AutoMapper-style runtime reflection. |
| [002](002-navigation-populators.md) | Navigation populators | `INavigationPopulator<T>` for cross-container/cross-source eager loading. |
| [003](003-outbox-dual-dispatch.md) | Outbox dual dispatch | Outbox + in-process dispatch + background processor; at-least-once delivery. |
| [004](004-authentication-dual-fetch.md) | Cross-service token validation (JWKS) | Extracted services validate Identity's RS256 tokens via JWKS / OIDC discovery (no shared key); HS256 shared-secret stays the monolith default; discovery is gateway-routed with a direct-Identity fallback. |
| [005](005-soft-delete-vs-erasure.md) | Soft-delete vs. erasure | Soft-delete stays for lifecycle; `IAnonymizable` + outbox purge for GDPR/CCPA erasure. |
| [006](006-database-per-service.md) | Database per service | Each service owns its DB + outbox; one `SQLServerDbContext` class, one instance per DB. Removed the shared-outbox race (2026-06-07). |
| [007](007-grpc-extraction.md) | gRPC cross-service calls | `*.Contracts` + typed clients + `Result`-over-the-wire for synchronous inter-service calls. |
| [008](008-service-extraction-topology.md) | Monolith → services + Gateway | One service host per module (the monolith with one module enabled), fronted by a YARP Gateway; transport at the edge keeps it reversible. |
| [009](009-resilience-and-recovery-objectives.md) | Resilience & recovery objectives | Standard resilience handler on every outbound client (fitness-enforced); consumers must declare RTO/RPO + drilled restore + single-region acceptance. |
| [010](010-integration-event-schema-versioning.md) | Integration-event schema versioning | Every integration event carries a `SchemaVersion` (default 1, fitness-enforced); breaking changes use a new event type + upcaster, never a silent reshape. |
| [011](011-single-locale-i18n.md) | Single-locale by design (no i18n) | en-US only is a deliberate, revisitable non-goal (rubric §27); multi-locale would be greenfield work. |
| [012](012-grpc-host-transport.md) | gRPC-host transport convention | Two coherent profiles; the Kestrel choice forces the gateway-forward mode + JWKS routing. **Both consumers now use Profile A** (`Http2`-only h2c + gateway-routed JWKS) after Store converged on 2026-06-22; Profile B (`Http1AndHttp2` + ALPN) is retained only as the SignalR/WebSocket exception (ADC's Notification service). |
| [013](013-result-pattern.md) | Result pattern over exceptions | Expected failures are `Result`/`Result<T>` values with a transport-agnostic `ErrorType`; only the edge maps to HTTP/gRPC. Exceptions stay for the genuinely exceptional. |
| [014](014-cqrs-decorator-pipeline.md) | CQRS decorator pipeline | Thin `ICommandHandler`/`IQueryHandler` use cases behind a Scrutor decorator chain (FeatureGate → Logging → Caching → Validating → Transactional → Handler); the order is load-bearing. |
| [015](015-architecture-fitness-functions.md) | Architecture fitness functions | Invariants gate the build twice: a compile-time layer guard (MSBuild) + a shared NetArchTest rule library parameterized by `IArchitectureMap`, run identically across all four repos (Common / Store / ADC / Helpdesk). |
| [016](016-lockstep-versioning-masstransit-pin.md) | Lockstep versioning + MassTransit-v8 pin | All thirteen packages release at one version; consumers swept in one pass (no phased rollout). MassTransit is pinned to v8 (v9 needs a license) and the pin is a fitness-function build gate. |
| [017](017-request-idempotency.md) | HTTP request idempotency | `[Idempotent]` action filter dedups client retries via an `Idempotency-Key` header + cached replay (24h, `X-Idempotent-Replay`); distinct from ADR-003's handler idempotency. |
| [018](018-polyglot-persistence.md) | Polyglot persistence (per-engine sources) | Three storage engines (SQL Server / Cosmos / SQLite) behind one model; engine is a `[UseDataSource]` attribute on the entity config (the orthogonal `Engine` axis to ADR-006's `Name` axis). Plumbing shipped + tested; first non-SQL entity not yet in production. |
| [019](019-rate-limiting.md) | Layered rate limiting (authenticated-only global limiter) | An always-on global limiter caps authenticated callers per-user (default 300/min) and exempts infra (`/health`, `/alive`, `/.well-known`, gRPC) and anonymous traffic; anonymous abuse is covered by output caching + the login-protection service instead; named policies stay for opt-in per-endpoint tightening. |
| [020](020-permission-based-authorization.md) | Permission-based authorization over roles | A capability layer over RBAC: `[HasPermission("…")]` resolves to on-demand `perm:*` policies backed by a central role→permission `IPermissionRegistry`; modules declare grants additively via `AddPermissions`. Opt-in and backward-compatible (named role policies untouched; inert until a host grants). Adopted by ADC (Conference/Identity), not yet by Store. |
| [021](021-consumer-inbox-idempotency.md) | Consumer-side inbox idempotency | Opt-in inbox (`IInboxStore` / `EfInboxStore`, `MessageBus:EnableInbox`) dedups broker redeliveries by `MessageId`: `IntegrationEventConsumer` checks before handlers, records after success, in the consumer's own DB (unique index as the race guard). At-least-once-with-dedup (handlers stay idempotent for the crash window). The broker-consume sibling of ADR-003 / ADR-017. |
| [022](022-browser-session-cookie-auth.md) | Browser session-cookie auth (Blazor SSR) | HttpOnly `mmca_auth_access` / `mmca_auth_refresh` cookies carry the session; `SessionCookieAuthenticationHandler` reads claims during SSR prerender (no signature check — the API stays the boundary, ADR-004) so `[Authorize]` passes on fresh GETs; refresh token stays server-side, hydrated via `/auth/session/token`. BFF-style, SameSite=Lax + Sec-Fetch-Site CSRF guard. |
| [023](023-security-response-headers.md) | Security-response headers + pluggable CSP | Centralized hardened security-headers middleware (`AddCommonSecurityHeaders`) with an `ICspPolicyProvider` CSP seam; the baseline CSP omits `script-src`/`style-src` so it cannot break Blazor, and HTML hosts register their own policy (`BlazorCspPolicyProvider`). Adopted at both apps' Gateway + UI edges. |

## Writing a new ADR

Copy the structure of an existing record: **Status** (Proposed / Accepted / Superseded — date and
link when superseding), **Context** (the forces and the problem), **Decision** (what we chose, in
enough detail to implement), **Rationale** (why this over the alternatives), **Trade-offs** (what it
costs). Number sequentially (`NNN-kebab-title.md`) and add a row above. Keep ADRs short and
decision-focused; deep mechanics belong in the workspace-level `Docs/Architecture/ArchitecturalAnalysis.md` (outside this repo) or the per-project CLAUDE.md.

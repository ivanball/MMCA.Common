# Changelog

All notable changes to the MMCA.Common packages are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow [Semantic Versioning](https://semver.org/)
and are derived from git tags by MinVer (see [VERSIONING.md](VERSIONING.md)).

## [Unreleased]

### Fixed
- **E2E auth helpers force WebAssembly interactivity before submitting.** `MMCA.Common.Testing.E2E`'s
  `LoginAsync`/`RegisterNewUserAsync` now wait for the Blazor WASM runtime to boot (then reload) before
  filling/submitting the auth form, via the new `PageExtensions.EnsureWasmInteractiveAsync` and the
  `E2E_WASM_READY_TIMEOUT` knob. Under InteractiveAuto a cold load runs the page on the **Server
  circuit**, so the auth POST is a contended UI-circuit→gateway→Identity **double hop** — the cause of
  the recurring CI "Registration failed: One or more errors occurred." reds on the 2-core runner.
  Forcing WASM makes it a single browser→gateway→Identity hop. Best-effort: falls back to Server-mode
  submit if WASM doesn't boot within the budget (behaviour unchanged from before).

## [1.71.0] - 2026-06-19

### Added
- **Broker retry policy.** `AddBrokerMessaging` now configures `UseMessageRetry` (exponential backoff)
  on both the RabbitMQ and Azure Service Bus transports. Tunable via new `MessageBus:RetryLimit`,
  `MessageBus:RetryMinIntervalSeconds`, and `MessageBus:RetryMaxIntervalSeconds` settings.
- **`IAnonymizable`** erasure seam (`MMCA.Common.Domain`) for reconciling soft-delete with
  data-subject erasure requests. See ADR-005.
- **`OutboxCleanupService`** background service that purges processed outbox rows. New settings
  `Outbox:RetentionDays` (default 7) and `Outbox:CleanupIntervalHours` (default 6).
- **bUnit** component-test harness for the shared Blazor UI primitives.
- **`MMCA.Common.Testing.UI`** package — shared bUnit component-test infrastructure: a unified
  MudBlazor/auth-aware test base (`BunitComponentTestBase`), a dialog/popover/snackbar provider
  harness, and interaction helpers. Consumed by downstream apps so component tests stop duplicating
  the bUnit/auth setup. This brings the published set to **twelve** packages.
- **Outbox dead-letter metric** is now exported (`AddMeter("MMCA.Common.Outbox")`).
- Supply-chain hardening: NuGet lock files, package source mapping, dependency vulnerability
  auditing, an SBOM at release, and Dependabot.
- `SECURITY.md`, `VERSIONING.md`, and a published breaking-change policy.

### Changed
- **(Behavior)** Processed outbox rows are now purged after `Outbox:RetentionDays` (default **7 days**).
  Previously they were retained indefinitely. Set `Outbox:RetentionDays = 0` to restore the old behavior.

### Fixed
- `IntegrationEventConsumer` no longer claims (in a comment/log) a retry policy that was never
  configured — a real retry policy is now applied (see Added).

### Security
- Dependency vulnerability audit is enforced in CI (`dotnet list package --vulnerable`), and
  `nuget.config` restricts each package to its expected source via `packageSourceMapping`.

## [1.70.0] - 2026-06-19

### Fixed
- **R24 §8** — paginated list reads with a collection include returned empty child collections (e.g.
  `GET /Sessions?includeChildren` came back with empty `sessionSpeakers` while by-id reads populated
  them). `EntityQueryPipeline` now forces `AsSplitQuery` when a child-collection navigation is included,
  so `Skip`/`Take` pagination no longer truncates child rows. Adds `IQueryableExecutor.AsSplitQuery`
  (EF bridge guarded by `IsEfQuery` for in-memory queries) + unit tests.

## [1.69.0] - 2026-06-19

### Added
- **Integration-event schema versioning** (R17, ADR-010). `BaseIntegrationEvent` exposes a
  `virtual int SchemaVersion` (default `1`, serialized with the payload) so cross-service consumers
  have an explicit version signal; a fitness test asserts every concrete `IIntegrationEvent` declares
  it. Non-breaking — existing events stay at version 1; breaking event changes use a new event type +
  upcaster (see ADR-010).
- **OpenAPI generation helpers** (R8). `AddCommonOpenApi()` / `MapCommonOpenApi()` (the latter
  Production-guarded) in `MMCA.Common.API`, so service hosts expose `/openapi/v1.json` consistently.
  Adds a dependency on `Microsoft.AspNetCore.OpenApi`.

### Changed
- **(Security, behavior)** The default Content-Security-Policy (`MMCA.Common.Aspire` SecurityHeaders,
  R18) is hardened from `frame-ancestors 'none'` to
  `default-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'`.
  It omits `script-src`/`style-src`, so HTML/Blazor hosts (which register their own `ICspPolicyProvider`)
  are unaffected; API/Gateway hosts using the default get the stricter baseline.

### Fixed
- Corrected stale comments implying auth tokens live in browser `localStorage` (R18) — access tokens
  are held in-memory; the refresh token is in an HttpOnly cookie.
- **CI:** the dependency-audit gate now honors `NuGetAuditSuppress` (it previously re-flagged accepted,
  unpatched advisories and reddened every run); the coverage floor gates the unit tier with generated
  code excluded; and the release SBOM step is now a hard gate.

---

<!--
Release process: tag `vMAJOR.MINOR.PATCH` on `main`; MinVer + the release workflow pack and push.
Move the relevant Unreleased entries under a new `## [x.y.z] - YYYY-MM-DD` heading at release time.
-->

# Changelog

All notable changes to the MMCA.Common packages are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow [Semantic Versioning](https://semver.org/)
and are derived from git tags by MinVer (see [the published versioning policy](https://ivanball.github.io/docs/guides/common-VERSIONING.html)).

## [Unreleased]

## [1.185.0] - 2026-09-03

**Breaking:** `DeleteUserHandlerBase<TUser, TCommand>` gained a required `ICacheService` constructor
parameter, in the middle of the existing pair: the signature is now
`(IUnitOfWork unitOfWork, ICacheService cacheService, ILogger logger)`. Every subclass has to inject
`ICacheService` and forward it to the base. See [UPGRADING.md](UPGRADING.md) for the mechanical fix.

### Changed

- **The soft-deleted user marker is written by the shared erasure workflow** (ADR-047). After
  `SaveChangesAsync` succeeds, and before the app's `afterCommit` tail runs,
  `DeleteUserHandlerBase` writes `SoftDeletedUserCache.MarkDeletedAsync(...)` itself, so every app
  gets the identical token-revocation window instead of one app hand-rolling it and another shipping
  none. The write is best effort: a cache fault is caught (bar cancellation) and logged as a warning,
  never turned into a failure the caller would retry against an already-erased account. It runs ahead
  of the app tail deliberately, so unbounded post-commit app work cannot stretch the window in which
  a deleted account's already-issued access token still passes the API middleware check.
- Apps that queued their own marker write on `afterCommit` should delete it; the base now writes it
  first, and a duplicate write only re-stamps the same key.

## [1.184.0] - 2026-09-03

**Breaking:** second feature-by-folder pass (rubric §5), this time over the eight flat public
namespaces the first pass left alone because every consumer imports them. Namespaces follow folders
(IDE0130), so each bucket below was split by concern; no type, member, signature, configuration key,
database object or behavior changed, and the consumer sweep is a mechanical `using` rewrite
(`Tools/Scripts/move-namespace.ps1` in the workspace applied it to ADC, Store and Helpdesk in the same
release). The full old-to-new map with every type, and the fix for a consumer outside the workspace,
is in [UPGRADING.md](UPGRADING.md).

### Changed

- **`MMCA.Common.Application.Interfaces.Infrastructure` dissolved** into `.Persistence`
  (repository, unit of work, query execution, data sources, outbox administration),
  `.Notifications` (push, native push, device registrar, live channel, recipient provider),
  `.Storage` (file storage, image processing), `.Auth` (current user, password hasher, token
  service, soft-deleted user validator) and `.Mail` (`IEmailSender`); the five names are the
  `MMCA.Common.Infrastructure` folders that implement them.
- **`MMCA.Common.Application.Interfaces`** keeps the six cross-cutting contracts (cache,
  correlation, distributed lock, scheduled job, tenant, audit trail reader) and gained `.Events`
  (event bus, upcasters, domain and integration event handlers), `.Mapping` (DTO mapper, projector,
  entity query service, `ICreateRequest`) and `.Navigation`.
- **`MMCA.Common.Application.UseCases`** keeps `CqrsContractInspector` and gained `.Contracts`
  (`ICommand`, `IQuery`, the handler interfaces, `ICommandWithRequest`), `.Markers` (the six
  decorator opt-in interfaces) and `.Crud` (the create, update, delete and child-entity handler
  bases). `UseCases.Decorators` is unchanged.
- **`MMCA.Common.Shared.Auth`** keeps the claim types, role names and `ClaimsPrincipalExtensions`
  and gained `.Requests` (the eight auth request records), `.Responses` and `.Permissions`
  (the permission registry and its builder).
- **`MMCA.Common.Shared.ValueObjects`** keeps `ValueObject` and `Enumeration` and gained
  `.Contact` (`Address`, `Email`, `PhoneNumber` and their invariants), `.Financial` (`Money`,
  `Currency`) and `.Time` (`DateRange`, `DateTimeRange`).
- **`MMCA.Common.API.Startup`** keeps the host and module extensions and gained `.Pipeline`
  (the middleware pipeline builder), `.Endpoints` (app association, JWKS, OIDC discovery, OpenAPI)
  and `.Auth` (JWT authority, insecure-metadata warning filter). Extension-method call sites
  (`MapJwksEndpoint`, ...) surface the move as CS1061, not as a missing type.
- **`MMCA.Common.Testing` dissolved** into `.Fixtures` (SQL Server, Service Bus emulator and
  cross-service fixture bases, `IntegrationTestBase`, `ProductionHostApplicationFactory`),
  `.Conformance` (the eight ADR-058 runtime contract bases) and `.Support` (JWT generator, polling,
  recording forwarder, DI assert, feature-management and rate-limiter test extensions,
  `HandlerTestBase`). `Testing.Builders` is unchanged.
- **`MMCA.Common.Infrastructure.Persistence.Outbox`** keeps `OutboxMessage` and gained
  `.Processing` (processor, finalizer, signal, metrics, event name resolver) and `.Administration`
  (administration, cleanup, disabled notice, `OutboxSettings`).
- `FolderWidthTests` in this repo now exempts only `Application/UseCases/Decorators`, its test
  twin and `Domain/Interfaces` (one concept each); the five other exemptions are gone.

### Added

- `UPGRADING.md` at the repo root: one section per breaking release, newest first, with the
  old-to-new namespace map and the mechanical fix; retro-filled for v1.183.0.

### Consumer action

- Rebuild against the new namespaces: replace each old `using` with its successors listed in
  `UPGRADING.md` (or run the workspace `move-namespace.ps1` map), let IDE0005 drop the extras, then
  add the `using` behind any CS1061 on an extension method. No configuration, database or behavior
  change.

## [1.183.0] - 2026-09-02

**Breaking:** feature-by-folder reorganization of the framework packages (rubric §5). Namespaces
follow folders (IDE0130), so the buckets below were renamed; no type, member or behavior changed, and
the consumer sweep is a mechanical `using` rewrite (`Tools/Scripts/move-namespace.ps1` in the
workspace applied it to ADC, Store and Helpdesk in the same wave).

### Changed

- **`MMCA.Common.Infrastructure.Services` and `.Settings` dissolved** into the features they
  implement or configure: `Messaging` (+ `Messaging.Consumers`), `Notifications.Push`,
  `Notifications.Live`, `Storage`, `Auth`, `Context` (current user, correlation, tenant),
  `Mail`, `Scheduling`, `Caching`, `Persistence` (+ `.DataSources`, `.AuditTrail`, `.Outbox`,
  `.Tenancy`). `NotificationHub` moved from `Infrastructure.Hubs` to `Infrastructure.Notifications`
  (it was one half of a namespace cycle with the senders that use it).
- **`MMCA.Common.UI.Services.Capabilities`** (with its `Browser` and `Fallbacks` sub-namespaces)
  regrouped by capability family: `Accessibility`, `Auth`, `DeviceStatus`, `DeviceStorage`,
  `Geo`, `Interop`, `Media`, `Navigation`, `Notifications`; each family holds the contract, the
  browser implementation and the null fallback together. `MMCA.Common.UI.Maui.Capabilities` mirrors
  the same families.
- **`MMCA.Common.UI.Services`** root grab-bag split into `Services.Api` (the typed HTTP service
  bases and `HttpResultExecutor`), `Services.Culture`, `Services.Preferences`, `Services.Navigation`
  (the public link builder joins it); `ThemeService` joined `MMCA.Common.UI.Theme`.
  `MMCA.Common.UI.Services.Auth` gained `Auth.Tokens` (token storage and refreshers) and `Auth.OAuth`.
- **`MMCA.Common.UI.Components`** split into `Components.PageState`, `Components.Lists`,
  `Components.Sharing`, `Components.Forms`, `Components.Auth`; the theme components joined
  `MMCA.Common.UI.Theme` and the culture components joined `MMCA.Common.UI.Globalization`.
  Consumer `_Imports.razor` files need the new `@using` lines (the script adds them).
- `MMCA.Common.Testing.Architecture` is folder-only reorganized (`Rules/{Topic}/`, `Bases/{Topic}/`);
  its namespace is unchanged, it stays the flat public surface consumers subclass (ADR-015).
- The accepted `MMCA.Common.Infrastructure` namespace cycle allowance names `Messaging` where it
  named the dissolved `Settings` (same three-node component, same edges, see `NamespaceCycleTests`).

### Added

- **Folder-width fitness rule** (`ArchitectureRules.FoldersStayNarrow` + `FolderWidthTestsBase`,
  rubric §5): a folder under `Source/` or `Tests/` holds at most 12 direct code files (a Razor
  component with its code-behind and resx counts once; `Migrations/`, MAUI `Platforms/` and
  `Resources/` are skipped; a repo lists its own documented exemptions). Every repo subclasses it,
  so the layout is a CI merge gate like the other layout rules.

### Consumer action

- Rebuild against the new namespaces: run the workspace `move-namespace.ps1` maps or fix the
  `using` lines the compiler reports. No configuration, database or behavior change.

## [1.182.0] - 2026-09-02

Cost release: health-probe traces stay out of Application Insights / Log Analytics by default. No
consumer action is required at bump time; hosts that want to see their own probe traces set
`Telemetry:FilterProbeTelemetry=false`.

### Added

- **`Telemetry:FilterProbeTelemetry` cost knob** (`MMCA.Common.Aspire`, rubric §31). Keeps
  health-probe traces out of Application Insights / Log Analytics: the ASP.NET Core instrumentation
  refuses inbound `/alive`, `/health` and `/health/*` requests, the HttpClient instrumentation
  refuses outbound calls to the same paths (YARP active health checks and the gateway's
  `DownstreamServiceHealthCheck` probes, which have no request ancestor), and a new
  `ProbeTelemetryFilterProcessor` un-records the dependency spans hanging off a probe request (the
  health check's SQL `SELECT 1`, the Redis PING). Container Apps probes, gateway aggregate probes
  and the availability web test accounted for 100% of the AppRequests rows in both production
  workspaces, and `Telemetry:TracesSampleRatio` does not reduce them. **Defaults to `true`**, unlike
  the metrics knobs: a host that wants to see its own probe traces sets it to `false`. Metrics are
  deliberately untouched, so `http.server.request.duration`, Kestrel and routing instruments keep
  feeding dashboards.
- **`HealthEndpointPaths`** (`MMCA.Common.Aspire`). The `/health`, `/alive` and `/health/ready`
  paths `MapDefaultEndpoints()` maps, plus an `IsProbePath` predicate, declared once so the mapping
  and the probe telemetry filters cannot drift apart.

## [1.181.0] - 2026-09-02

Readiness-safe Redis registration and authoritative metric cost toggles. One consumer action is
REQUIRED at bump time: replace direct `builder.AddRedisDistributedCache("redis")` /
`builder.AddRedisClient("redis")` calls in service hosts with `builder.AddRedisCaching()` (and
`AddRedisOutputCaching()` where a Redis-backed output cache is used). Without that swap the Aspire
integrations keep registering their untagged `StackExchange.Redis` health check, which lands in
`/health/ready` and, under StackExchange.Redis 3.x against Azure Managed Redis, fails every probe
with `This operation is not available unless admin mode is enabled: CLUSTER`, so a new Container
Apps revision never activates while the previous one keeps serving.

### Added

- **`AddRedisCaching` / `AddRedisOutputCaching`** (`MMCA.Common.Aspire`, `RedisCachingExtensions`).
  Framework-owned Redis registration for hosts: wraps Aspire's distributed-cache and client
  integrations with their automatic health checks disabled (they arrive untagged and therefore gate
  readiness) and the output-cache integration alongside. Both are no-ops when the named connection
  string is absent, so a host without Redis is unchanged.

### Changed

- **The `redis` infrastructure health check is PING-only** (`MMCA.Common.Aspire`). It is now a
  Common-owned `RedisPingHealthCheck` (name `redis`, tag `optional`, singleton, resolves the DI
  `IConnectionMultiplexer` or lazily owns one) that issues a single `PING`. Readiness checks are
  PING-class, never admin-class (ADR-025). The `AspNetCore.HealthChecks.Redis` dependency is
  removed; it still arrives transitively through Aspire.

### Fixed

- **`Telemetry:DisableHttpClientMetrics` and `Telemetry:DisableRuntimeMetrics` now hold under the
  Azure Monitor distro** (`MMCA.Common.Aspire`). Skipping the OpenTelemetry instrumentation call was
  not enough because `UseAzureMonitor()` subscribes the `System.Net.Http` meter itself; the toggles
  now also register `MetricStreamConfiguration.Drop` views for `System.Net.Http`,
  `System.Net.NameResolution` and `System.Runtime`, so the instruments are dropped whoever added the
  meter.

## [1.180.0] - 2026-09-01

The edge rate limiter learns to recognize a synthetic capacity proof. Additive: one new optional
configuration section entry, off by default; nothing is required of a consumer at bump time beyond
the pin unless it wants the bypass, in which case it supplies the secret from its secret store.

### Added

- **Secret-gated synthetic-traffic bypass for the edge rate limiter** (`MMCA.Common.Aspire`).
  `GatewayRateLimitingSettings` gains `SyntheticTrafficHeaderName` (default
  `X-Synthetic-Traffic-Key`) and `SyntheticTrafficSecret`; a request presenting that header with
  that secret takes the no-limiter partition on BOTH chained limiters, exactly as a bypassed path
  does. A scheduled capacity proof drives its whole run from ONE runner IP, which the per-IP fixed
  window cannot tell from an unauthenticated flood, so the run measured the limiter instead of the
  system. Off by default: the bypass cannot be claimed while the secret is unset, the comparison is
  constant time over UTF-8 bytes, exactly one header value is accepted, and a configured secret
  shorter than 32 characters fails at registration. Supply it from a secret store or the
  environment (`GatewayRateLimiting__SyntheticTrafficSecret`), never from a checked-in
  `appsettings` file.

## [1.179.0] - 2026-09-01

Bug-hunt remediation (2026-09-01 run): a refresh-token rotation race, a non-atomic push-notification
send, request validators that were silently dropped, and a set of UI, API and test-package defects.
Everything is additive: no signature breaks and no schema changes, so nothing is required of a
consumer at bump time beyond the pin.

### Fixed

- **Concurrent refresh-token rotation is now claimed atomically** (`MMCA.Common.Application`,
  `MMCA.Common.Infrastructure`). Two requests presenting the SAME still-live refresh token each read
  their own un-revoked copy of the session row, so both used to mint a successor and the presented
  row could never fire reuse detection again, leaving one permanently undetected extra session.
  Rotation now runs through the new `IRefreshSessionStore.TryRotateAsync`, which the EF store
  implements as a conditional `UPDATE ... WHERE Id = @id AND RevokedAt IS NULL` plus the successor
  insert in one transaction. The request that loses the claim gets the answer a replay gets: the
  user's whole live family is revoked (BR-206) and the refresh fails with `Auth.InvalidRefreshToken`.
  No schema change and no migration: the row still carries no concurrency token.
- **`SendPushNotificationCommand` is `ITransactional`** (`MMCA.Common.Application`). The handler
  committed the audit row (carrying `DedupKey`) before the per-recipient rows and the sends, so a
  fault in between left a committed row nothing ever delivered, and every retry with that key
  short-circuited on the dedup lookup and reported success forever. The whole sequence is now one
  unit, so a failed attempt rolls back and a retry re-runs the send. The sender calls now run inside
  the transaction.
- **The retry policy disposes every retried HTTP response** (`MMCA.Common.UI`). Polly hands the
  caller only the final outcome, so each intermediate 5xx/408/429 attempt leaked its content buffer
  and kept its connection out of the handler pool until finalization, under exactly the sustained
  backend failure the retries exist to survive. The final response is still the caller's to dispose.
- **`DeepLinkDispatcher.Publish` decides under its lock** (`MMCA.Common.UI`). The handler was read
  outside the lock, so a native callback publishing while a listener was subscribing and draining
  could buffer the route after that drain, stranding it with no future consumer (the warm-boot deep
  link on a MAUI head). The raise-or-buffer decision is now one locked step; the event is still
  invoked outside the lock.
- **The Admin nav section is hidden from anonymous visitors** (`MMCA.Common.UI`). It was gated on
  item count alone, unlike the User section beside it, so a module registering a `Section.Admin`
  `NavItem` without a `RequiredRole` (which defaults to null) disclosed the feature's existence and
  URL. Consumers that already set a role on every admin item see no change.
- **`ApiFileDownloadButton` sanitizes `FileName` before staging** (`MMCA.Common.UI`). The parameter
  went into `Path.Combine(Path.GetTempPath(), FileName)` verbatim, so a rooted value discarded the
  temp root and `..` segments walked out of it: reachable for a consumer that builds the name from
  entity data. Directory segments are now stripped to the bare file name, and an unusable name warns
  without even performing the download.
- **The mobile card fetch no longer overwrites the persisted `RowsPerPage`** (`MMCA.Common.UI`).
  `LoadMobileDataAsync` saved `MobilePageSize` into the state the desktop grid shares, so narrowing
  the viewport replaced a user's 50-rows-per-page choice with 10 on the next visit. It now saves 0,
  which the restore guards skip, exactly as the virtualized path already did.
- **`E2ETestBase` no longer reports success on a silent auth failure**
  (`MMCA.Common.Testing.E2E`). `LoginAsync` and `RegisterNewUserAsync` raced three signals and
  returned normally when ALL of them timed out, which is what a submit that fails with neither a
  navigation nor a rendered error alert looks like (a 500 that renders nothing, a dropped request, a
  JS exception mid-submit). The caller's follow-up interactivity wait was already satisfied by the
  still-rendered auth page, so the helper reported a sign-in that never happened. The four-way
  classification is now explicit (`AuthOutcomeRules.Classify`) and the silent case throws an
  `InvalidOperationException` naming the operation, the budget and the URL. The losing waits are also
  observed, so their timeouts cannot surface as unobserved task exceptions elsewhere in a run.
  **Consumer-visible at bump time:** an E2E suite whose login is genuinely broken but was passing
  silently will turn RED. That is the point, but expect it.
- **`ConfigureTestFeatureFlags` layers onto the host configuration** (`MMCA.Common.Testing`). It
  built a flags-only `IConfiguration` and registered it, and .NET DI hands a non-collection
  dependency the LAST registration, so anything constructed afterwards that injects `IConfiguration`
  directly saw no connection strings, no authentication settings and no data sources, contradicting
  the helper's own docstring. It now chains the host's configuration and adds the flags on top (the
  flags still win). Behind a factory registration of `IConfiguration` there is nothing to read, and
  the flags stand alone exactly as before.
- **Module isolation covers the full internal-layer cross product**
  (`MMCA.Common.Testing.Architecture`). Six rules checked each internal layer against its own layer
  in other modules, plus Domain and Application against another module's Infrastructure. A module's
  Domain reaching another module's Application or Api (and the other unchecked pairs) passed every
  gate: the per-module layer rules forbid only the SAME module's higher layers, and the compile-time
  guard only knows `MMCA.Common.*` references. New `ArchitectureRules.ModuleInternalLayersAreIsolated`
  plus a `ModuleIsolationTestsBase` fact close it. UI is deliberately excluded (a module's UI
  composing another module's UI is intended). **Consumer-visible at bump time:** the new fact runs in
  every repo that subclasses the base; ADC and Store cross-module project references are Shared-only
  today, so it should be green.
- **`ApnsTokenBridge` re-arms its rendezvous per registration attempt** (`MMCA.Common.UI.Maui`). A
  single one-shot `TaskCompletionSource` handed every later caller the first attempt's outcome
  instantly, so after a failed APNs registration the next `GetTokenAsync` re-registered and then
  reported failure without waiting for the callback it had just triggered. `WaitForTokenAsync` now
  completes on the NEXT callback, and `Publish` swaps in the new source before completing the old
  one. Call `WaitForTokenAsync` before asking UIKit to register, as the shipped provider does.
- **`NotificationBell` cannot start a poll loop after disposal** (`MMCA.Common.UI`). Disposal during
  the first unread-count read left it creating a `PeriodicTimer` nothing would dispose and starting a
  loop whose first act was to read an already-disposed `CancellationTokenSource`, faulting a
  discarded task. It re-checks after the await, and the loop also catches `ObjectDisposedException`.
- **`OwnerOrAdminFilter` parses owner ids invariantly** (`MMCA.Common.API`). The route-value and
  bound-argument parses used the host's ambient culture, unlike every other machine-data parse in the
  framework. They now pass `NumberStyles.Integer` and `CultureInfo.InvariantCulture`. The filter
  already failed closed on a non-parse, so this is a convention fix rather than a security fix.
- **`CommandRequestValidator` runs every registered `IValidator<TRequest>`**
  (`MMCA.Common.Application`). It took `FirstOrDefault()`, so a module that authored a validator
  beside a framework-supplied one for the same request type had one of them silently turned into
  dead code, in DI registration order. All of them now run and their failures are unioned, matching
  the policy the command and query decorators already apply to `IValidator<TCommand>` (ADR-014);
  duplicate registrations of one validator class are de-duplicated by runtime type. A module with two
  validators for one request will now surface failures that were previously not reported.

### Added

- **`LatestLoadGuard`** (`MMCA.Common.UI`, `MMCA.Common.UI.Common`). Small disposable helper for
  routed detail pages: `Begin()` cancels the previous load and hands back a token plus a generation,
  and `IsCurrent(generation)` says whether that load's result may still be assigned. Blazor reuses a
  routed component instance across route-parameter changes, so a slow load for one id otherwise
  overwrites the page after a faster load for the next id has rendered. Not thread-safe by contract
  (renderer synchronization context only).
- **`IRefreshSessionStore.TryRotateAsync`** (`MMCA.Common.Application`). Additive interface member
  with a default implementation (revoke, add, save), so an existing custom or test store keeps
  compiling and behaving as it did; the shipped EF store overrides it with the database-arbitrated
  claim described above.

## [1.178.0] - 2026-09-01

Two move-to-Common extractions from the 2026-08-31 drift run: the E2E gateway rate-limit lift both
AppHosts carried inline, and the Azure Service Bus emulator test fixture both consumers hand-copied.
Both are additive, and both stay inert until a consumer opts in. No breaking changes.

### Added

- **`WithE2eGatewayRateLimitLift`** (`MMCA.Common.Aspire.Hosting`). AppHost extension on a
  `ProjectResource` (the gateway) that lifts the edge rate limiter for an E2E run: it reads
  `E2E_LIFT_REGISTRATION_THROTTLE` itself, OR-ed with an optional `alsoLiftWhen` call-site flag for a
  host that implies the lift from another E2E switch of its own, and when triggered sets
  `GatewayRateLimiting__PermitLimit`, `GatewayRateLimiting__GlobalConcurrencyLimit` and
  `MmcaGateway__RateLimiterPolicies__auth-tight__PermitLimit`. Untriggered it returns the builder
  unchanged, so it is a no-op locally and in production, where the gateway keeps the real limits from
  its own `appsettings.json`. The lift exists because a whole E2E suite arrives from ONE loopback
  client IP, which the per-IP fixed window reads as the single-source flood it was built to stop. A
  unit test cross-asserts the emitted keys against `GatewayRateLimitingSettings.SectionName` and
  `GatewaySettings.SectionName`, so a section rename cannot silently orphan the lift. Mirrors the
  shipped `WithE2eRegistrationThrottleLift`, and both lifts now read one shared trigger constant.
- **`ServiceBusEmulatorFixtureBase`** (`MMCA.Common.Testing`). Collection-fixture base for the Azure
  Service Bus emulator broker-parity tier, beside `CrossServiceFixtureBase`. It owns the pinned
  emulator container (`DefaultEmulatorImage` 2.0.1, overridable through a virtual `EmulatorImage`),
  the process-global MassTransit v8 entity-default override the emulator's one-hour TTL quota
  requires, the AMQP and admin-plane (5300) clients with a pure static
  `ComposeAdminConnectionString`, and wall-clock-bounded start and stop phases (virtual
  `ContainerStartTimeout` / `BusStartTimeout` / `BusStopTimeout` with named PHASE 1 / PHASE 2
  `TimeoutException` text, so a hang names its phase instead of being killed at the job timeout with
  its log discarded). Hosting the tier's one bus is opt-in through virtual `ReceiveQueueName` plus
  `ConfigureReceiveEndpoint`, with a `Consumed` bag on the base; the base provisions exactly ONE
  receive endpoint, so each additional contract costs a topic and a subscription rather than another
  queue against an admin plane throttled at roughly one operation per second. The sealed subclass,
  the `[CollectionDefinition]` class, the integration-event contracts and the assertions stay
  app-side. Adds `Testcontainers.ServiceBus`, `Azure.Messaging.ServiceBus` and
  `MassTransit.Azure.ServiceBus.Core` (v8 by policy) to the package, the same Docker-only test-tier
  profile as its existing Testcontainers references.

### Dependencies

- `System.Linq.Dynamic.Core` 1.7.3 -> 1.7.4.
- `Meziantou.Analyzer` 3.0.190 -> 3.0.200; no new finding at error severity, so the shared
  `.editorconfig` baseline is unchanged.
- New test-tier pins for the fixture base above: `Testcontainers.ServiceBus` 4.14.0 and
  `Azure.Messaging.ServiceBus` 7.20.2 (the first line with working emulator admin-plane support).
  `MassTransit.Azure.ServiceBus.Core` reuses the existing 8.5.10 pin.

## [1.177.0] - 2026-08-31

Write-side registration and validation quality-of-life: `AddEntityCrud` now completes the update
command's validator bridge on its own, and the common validation rules gain identifier-field bases,
so consumers stop hand-registering the bridge and stop re-deriving per-app id rule classes. No
breaking changes.

### Added

- **`RequiredIdRules<T, TId>`** (`MMCA.Common.Application`). Reusable rules for a required
  identifier field via `NotEmpty` (rejects zero for an integer key and `Guid.Empty` for a Guid key).
  The field phrase interpolates verbatim into "You must specify {fieldName}", so the caller supplies
  the article and any qualifier ("a Category", "an Event for the Session") and existing consumer
  messages can be preserved byte-exact; optional error code like the other rule classes.
- **`OptionalPositiveIdRules<T, TId>`** (`MMCA.Common.Application`). Reusable rules for an optional
  identifier field: null passes, a supplied value must be positive (`GreaterThan(default)`, which
  FluentValidation skips for null, so no `When` clause and no per-validation compiled selector).

### Changed

- **`AddEntityCrud` registers the update command's validator bridge** (`MMCA.Common.Application`).
  The same `CommandRequestValidator` bridge `AddEntityUpdateVerb` and `AddEntityUpdate` already
  register now also closes over `UpdateEntityCommand<TEntity, TUpdateRequest, TIdentifierType>`
  inside `AddEntityCrud`, so a module's update-request rules run in the validating decorator without
  a hand-registered bridge. `TryAdd` semantics: an explicitly registered `IValidator` for the
  command still wins, so existing hand-registered bridges in consumers are harmless no-ops until
  they are deleted.

## [1.176.0] - 2026-08-31

Local-vs-prod broker parity for consumers: the Aspire AppHost can now provision the Azure Service Bus
emulator as an opt-in local broker, and the MassTransit registration drives it through the same
AzureServiceBus provider path used in production. Plus a client-side absolute-URL validation attribute
and per-module layer-requirement overrides for the architecture-test bases. No breaking changes.

### Added

- **`AddServiceBusEmulatorBroker` + `ServiceBusEmulatorResource`** (`MMCA.Common.Aspire.Hosting`).
  Provisions the official Service Bus emulator container (2.0.1, admin plane on 5300) wired to an
  existing SQL Server resource, exposing an emulator-form connection string
  (`UseDevelopmentEmulator=true`) and the admin-plane endpoint. A new `WithBroker` overload accepts
  the emulator resource and sets `MessageBus__Provider=AzureServiceBus`,
  `MessageBus__ConnectionString`, and `MessageBus__EmulatorAdminEndpoint` on the service; the
  RabbitMQ overload is unchanged.
- **Service Bus emulator support in `AddBrokerMessaging`** (`MMCA.Common.Infrastructure`). When the
  AzureServiceBus connection string carries `UseDevelopmentEmulator=true`, the bus is configured via
  the MassTransit v8 custom-clients `Host` overload (the only v8 path onto the emulator) with an
  admin client built from the new `MessageBusSettings.EmulatorAdminEndpoint`, and the process-global
  transport TTL quotas are lowered once to emulator-accepted values. The real Azure Service Bus path
  is byte-for-byte unchanged.
- **`AbsoluteUrlAttribute`** (`MMCA.Common.UI`). DataAnnotations mirror of the server-side
  `AbsoluteUrlRules` semantics (absolute http/https only, null/empty passes; pair with `[Required]`),
  with resource-key `ErrorMessage` localization like the other model attributes.
- **`ModuleRequiredLayerOverrides`** (`MMCA.Common.Testing.Architecture`).
  `LayerDependencyTestsBase` can now require a different layer set for a named module (a deliberately
  thin module without Domain/Infrastructure/UI assemblies) without weakening the required set for
  every other module; backed by a new three-argument `ArchitectureRules.ModulesDeclareLayers`
  overload. The existing overloads are unchanged.

## [1.175.0] - 2026-08-31

A six-front implementation-lift wave (scorecard categories 8, 19, 23, 25, 26, 34): migrations proven
in CI, cascade soft-delete enforced, a complete default CSP with a nonce path, an explicit client-side
staleness policy, opt-in desktop grid virtualization, and a typed notification deep link. Three
constructor signatures change shape (binary-breaking, source-compatible via optional parameters);
each is listed with the call site a consumer has to touch.

### Changed (breaking)

- **`EntityServiceBase` constructor gains an optional `IUiReadCache? readCache = null`**
  (`MMCA.Common.UI`). Source-compatible (existing subclasses compile unchanged) but binary-breaking:
  recompile during the bump. Passing `null` keeps read behavior byte-identical; passing the
  DI-registered cache opts that service's reads into the staleness policy below. `AuthUIService` and
  `NotificationState` change the same way (`IUiReadCache?` and `TimeProvider?` respectively, both
  optional and defaulted).
- **The default static Content-Security-Policy now carries `script-src` and `style-src`**
  (`MMCA.Common.Aspire`). `SecurityHeadersSettings.ContentSecurityPolicy` defaults to
  `default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'`,
  the strength Blazor and MudBlazor require, so a host on the static default gets a complete policy
  instead of one missing both directives. A host that loads third-party scripts or stylesheets from
  another origin must now name them in a configured policy; everything same-origin keeps working.
- **`BlazorCspPolicyProvider` fails closed on a missing or unparseable API endpoint**
  (`MMCA.Common.UI.Web`). The fallback was `connect-src 'self' https: wss:` in Report-Only mode
  (permissive and unenforced); it is now `connect-src 'self'`, enforced. A misconfigured endpoint
  surfaces as blocked API calls in the browser console instead of a silently unenforced policy; fix
  the `ApiEndpoint`/`WasmApiEndpoint` setting rather than relaxing the policy.
- **`NotificationBell` reads its cadence from configuration** (`MMCA.Common.UI`). The hardcoded 30s
  poll moves to `NotificationBellOptions` (`NotificationBell` section: `PollInterval`,
  `NavigationRefreshMaxAge`, both defaulting to 30s), and navigating no longer refetches the unread
  count unless it is stale by that policy. Defaults preserve today's cadence; hosts tune via config.

### Added

- **Client-side staleness policy** (`MMCA.Common.UI`): `IUiReadCache` (scoped, per circuit) with
  `UiReadCacheOptions` (`UiReadCache` section: `Enabled`, `DefaultTtl` 60s, longest-prefix
  `RoutePrefixTtls`). Keys are the relative URL (path plus full query), the same shape as the server
  output cache's `QueryKeys="*"` (ADR-040), so client and server agree on what "the same read" means.
  Reads through `EntityServiceBase` route via `GetCachedAsync` (successes only, explicit
  `bypassCache` available); writes invalidate their endpoint prefix; logout clears the cache. Opt-in
  per service via the constructor parameter above.
- **Per-request CSP nonce path** (`MMCA.Common.Aspire`): a `{nonce}` placeholder anywhere in a
  configured policy is replaced per request with `'nonce-<base64>'`, and `CspNonce.Get(HttpContext)`
  exposes the value for the host layout to stamp onto its script and style tags. This is the
  supported path off `style-src 'unsafe-inline'` for hosts that can nonce their tags.
- **Cascade soft-delete fitness rule** (`MMCA.Common.Testing.Architecture`):
  `CascadeSoftDeleteConventionTestsBase` fails the build when an aggregate declaring a collection of
  auditable children has no `Delete()` override that cascades (`DeleteChildren` or per-child
  `Delete()`; a bare `base.Delete()` cannot satisfy the rule). Subclass with your map and move each
  reported aggregate to a fix or a justified exemption. Common now also runs the shipped hard-delete
  gate over its own assemblies, so a new framework eraser fails here rather than downstream.
- **Migrations proven in CI**: the `consumer-source-build` job applies MMCA.Helpdesk's real SQL
  Server migrations against an ephemeral SQL Server 2022 container with pinned `dotnet-ef`, and an
  in-repo fixture migration proves the `Migrate`/`None` startup strategies against real migration
  files in the unit tier.
- **Opt-in desktop grid row virtualization** (`MMCA.Common.UI`): override `VirtualizeGrid` on
  `DataGridListPageBase` and bind `Virtualize`/`Height`/`ItemSize`/`VirtualizeServerData` to the new
  `LoadVirtualizedServerDataAsync` funnel (the viewport window maps onto the existing paged API; a
  grid binds `ServerData` or `VirtualizeServerData`, never both). Scroll persistence follows the
  grid's inner container; the pager-restore machinery is inert for virtualized grids. Defaults off;
  existing grids are untouched.
- **Typed notification deep link** (`MMCA.Common.UI`): `/notifications/inbox/{Id:int}` highlights
  and scrolls to one notification (`NotificationRoutePaths.NotificationInboxItem(id)` builds the
  link); a malformed id renders `NotFound` via the route constraint, an absent id degrades to the
  plain inbox. A new navigation-contract fact requires every future route parameter to carry a type
  constraint.

### Fixed

- **`FACTS.md` names both registries**: the generated packages line now reads "Released in lockstep
  to nuget.org and GitHub Packages (dual-registry, ADR-053)", ending a seven-cycle drift between the
  generator prose and the actual release pipeline.

## [1.174.0] - 2026-08-30

Five framework riders feeding the consumer wave: one credential-verification path, one startup-gate
default, the MAUI token pipeline, a testing affordance and a send-page caption. Three of them are
breaking; each is listed below with the exact call site a consumer has to touch.

### Removed (breaking)

- **The legacy HMAC-SHA512 verification branch** (`MMCA.Common.Infrastructure`).
  `PasswordHasher.VerifyPassword` derives every candidate digest with PBKDF2-HMAC-SHA512: the
  128-byte-salt discriminator, the `ComputeLegacyHash` path and the `LegacyHmacSaltSize` constant are
  gone. A credential still stored in the pre-PBKDF2 shape now fails verification instead of
  authenticating through an unsalted single-round digest, so it needs a password reset. Both
  production databases were verified clean (every stored salt is 32 bytes) before the branch was
  removed; a consumer on an older data set should run the same check before taking this version.
  `PasswordHasherSecurityTests` pins the removal with a test asserting a legacy-shaped digest is
  rejected.

### Changed (breaking)

- **`WithH2cHealthCheck` defaults to `/alive`** (`MMCA.Common.Aspire.Hosting`). `DefaultProbePath`
  moves from `/health/ready` to `/alive`, because a startup `WaitFor` gate must probe liveness: a
  readiness endpoint aggregates downstream and warmup checks, so gating startup on it can deadlock the
  dependency graph when the warmup path runs back through the resource that is waiting. An AppHost
  that passes `/alive` explicitly can drop the argument; one that relied on the old default changes
  behavior silently, so check every `WithH2cHealthCheck` call site during the bump. Pass an explicit
  path where a stricter gate is genuinely wanted and no cycle exists.
- **`DirectApiTokenRefresher` takes `ISecureTokenStore`** (`MMCA.Common.UI`). Its second constructor
  parameter changes from `ITokenStorageService` to the new raw-storage interface. Hosts that wire the
  MAUI token pipeline through `AddCommonMauiTokenStorage()` need no change (it registers both halves);
  a host that constructs the refresher by hand, or registers its own storage, supplies an
  `ISecureTokenStore` instead.

### Added

- **`ISecureTokenStore`** (`MMCA.Common.UI`): raw token persistence with no freshness semantics,
  splitting storage from the freshness-checking layer above it. `MauiSecureTokenStore`
  (`MMCA.Common.UI.Maui`) implements it over OS SecureStorage with the same per-call guards the old
  storage type carried, and `AddCommonMauiTokenStorage()` registers it alongside the storage service.
- **`CapturedRequest.Headers`** (`MMCA.Common.Testing.UI`): every request header plus the content
  headers when the request had a body, keyed case-insensitively with multi-values comma-joined, so a
  test can assert `If-Match` or `Content-Type` directly. Additive and non-positional: the record's
  constructor and `Deconstruct` are unchanged, and `Authorization` still works as before.
- **`INotificationScopeProvider.GetCurrentScopeDisplayNameAsync`** (`MMCA.Common.UI`): a default
  interface method returning null, so existing implementations compile untouched. An application that
  scopes its notifications overrides it to name the current scope, and the send page captions the
  auto-applied target ("Targeting: {name}") instead of leaving the operator to infer who a broadcast
  reaches. Same never-throw contract as `GetCurrentScopeKeyAsync`; failing closed here means returning
  null, since a missing caption only hides information while a wrong one would state the wrong
  audience.

### Fixed

- **MAUI reads its access token through an expiry check.** `MauiTokenStorageService` now mirrors
  `WasmTokenStorageService`: a 30-second skew, `JwtTokenInfo.IsFresh`, and a single-flight refresh
  through `ITokenRefresher`, delegating raw reads and writes to `ISecureTokenStore`. It previously
  returned whatever the secure enclave held, so a token that expired while the app was backgrounded
  was handed to the delegating handler, the auth-state provider and SignalR alike, and the resulting
  401 read to the user as a random sign-out. The split also breaks the DI cycle that a freshness check
  would otherwise have introduced: storage depends on the refresher, and the refresher depends on the
  raw store rather than back on storage.

## [1.173.1] - 2026-08-30

### Fixed

- **Filter-built concurrency responses satisfy the problem-details contract.** The 428 (no
  precondition), 400 (malformed If-Match) and exception-path 412 responses from
  `SupportsIfMatchAttribute` are now built through the registered `ProblemDetailsFactory` (stamping
  `traceId`/`requestId` like every other problem response) and carry the standard `errors`
  extension (`Concurrency.PreconditionRequired`, `Concurrency.MalformedIfMatch`,
  `Concurrency.PreconditionFailed`). v1.173.0 answered them with a bare body, which failed the
  consumers' RFC 9457 contract tests.

## [1.173.0] - 2026-08-30

One way to do everything. This release removes the dual code paths that existed only for
compatibility with earlier package versions; supporting older consumers is a non-goal, so each pair
collapses to its single surviving mechanism. The pairs that serve the monolith-versus-extracted-services
choice are untouched by design: both message-bus transports, the per-engine context classes, the
HS256/RS256 signing choice, the outbox toggle and the two-event-paths model all remain.

### Removed (breaking)

- **The concurrency body transport** (`MMCA.Common.Shared` / `Application` / `API` / `UI`). The
  optimistic-concurrency token travels only in the `If-Match` header: a guarded endpoint answers 428
  to a request with no header, 400 to a malformed one and 412 to a stale one. `ConcurrencyTokenRequest`
  is deleted, `IConcurrencyAware.RowVersion` and `UpdateEntityCommand.RowVersion` are non-nullable (no
  token no longer means skip-the-check), both `SetOriginalRowVersion` overloads reject null, and
  `EntityServiceBase.UpdateAsync` sends the header itself (`ConcurrencyTagOf` exposes the current tag).
  `ConcurrencyETag` moved to `MMCA.Common.Shared.Http` so the UI package can format headers.
  `ConcurrencyConventionTestsBase` now asserts the inverse rule: no update request implements
  `IConcurrencyAware`.
- **The pre-registered role policies** (`MMCA.Common.API`). `AuthorizationPolicies` and the four
  fallback policies are gone; `IPermissionRegistry` permission policies are the one authorization
  model. The framework notification endpoints now require the `notifications:manage` permission
  (`NotificationPermissions.Manage`), which a host grants via `AddPermissions(...)`.
- **The settings-interface aliases** (`MMCA.Common.Application` / `Infrastructure`).
  `IApplicationSettings`, `IJwtSettings`, `ISmtpSettings`, `IConnectionStringSettings` and
  `IPushNotificationSettings` are deleted; `IOptions<T>` of the concrete settings class is the one
  resolution surface.
- **The EF-standard factory adapters** (`MMCA.Common.Infrastructure`). The
  `IDbContextFactory<TEngineContext>` registrations, `ApplicationDbContextEFFactory` and the
  per-engine default factories are gone; the framework `IDbContextFactory` (keyed by `DataSourceKey`)
  is the one factory surface, and its `GetDbContext(DataSource)` convenience overload is removed.
- **Members kept only for older callers.** The `[Obsolete]` tuple `FetchPage` on
  `MobileInfiniteScrollList` (use `FetchPageResult`), `ErrorMessages.Success(entityName, action)` and
  `ErrorMessages.ActionError(Exception, string)`, the two-argument caching-decorator constructors, the
  defaulted tenancy parameters on `DbContextFactory`, the string-frozen MAUI barcode-scanner overloads
  (the `Func<string>` forms remain), the five-argument `ModuleLoader.DiscoverAndRegister` and its
  silent AppDomain scan (`moduleAssemblies` is required, and `AddModuleHost` takes the module
  assemblies as its first parameter), the unscoped `IPushDeviceRegistrar.DeleteAsync` (the user-scoped
  overload with its ownership check is the one member), `DataAnnotationsModelValidator.Instance` (the
  localizer is required), the `NavItem` literal-title mode (`TitleResource` is required, positional
  slot 4) and the implicit string conversions on `Email` and `PhoneNumber` (use `.Value`).
- **Config switches whose only purpose was restoring prior behavior.**
  `DatabaseInitStrategy=EnsureCreated` (valid values: `Migrate`, `None`; anything else fails startup),
  `Outbox:TypeAliases` (`[EventName]` is the one stable-identity mechanism),
  `Outbox:PurgeDeadLetters` (dead-letter rows past their retention are always deleted) and
  `MmcaGateway:ForwardHttp2` (the gateway always forwards HTTP/2).
- **Migration-window mechanisms.** `HybridCacheService.RemoveByPrefixAsync` scans only its own `hc:`
  keyspace; `IdempotencyRecord.RequestBodyHash` is required (pre-field cache entries age out); the E2E
  `WaitForBlazorAsync` marker fallback for pages rendered by older UI packages is gone, and the
  `DomainInvariantViolationException` branches in `ErrorMessages` are gone (domain wording routes
  through `Result`).

### Changed

- **`Jwt:SigningAlgorithm` defaults to `RS256`.** Both algorithms remain supported (HS256 is the
  single-process monolith option); a host that wants HS256 sets it explicitly alongside
  `Jwt:SecretForKey`.
- **A `DataSources`-only configuration is self-sufficient.** When the top-level
  `ConnectionStrings:SQLServerConnectionString` is absent and the named `DataSources` entries declare
  exactly one distinct connection per engine, that database seeds `Default`, so framework-owned tables
  route without the legacy key; the plain `ConnectionStrings`-only monolith shape still works
  unchanged. Database health checks enumerate both shapes, deduplicated by connection string, and the
  Aspire `With*DataSource` host extensions no longer inject the `ConnectionStrings__*` twin.
  `AddInfrastructureHealthChecks` takes the single engine-agnostic `requireDatabase` flag.
- **`AuthenticationServiceBase<TUser>` requires `IOptions<RefreshSessionSettings>`** (the null
  fallback to pre-session behavior is gone; `RefreshSessions:Enabled` remains the per-host model
  toggle).
- **`QueryFilterService` fails closed.** A `DTOToEntityPropertyMap` entry whose dotted path cannot be
  walked is a `Filter.Property.NotFound` validation failure instead of a silent string comparison.

## [1.172.0] - 2026-08-30

Completes the generic write-side surface ADR-099 opened. The five shapes that still forced a
hand-written handler (a value derived before the mutation, an idempotent no-op, a fresh-scope retry,
a delete that has to load its children or refuse, a command that carries more than its request) now
sit on the framework bases. Every addition is additive: existing subclasses, commands and appliers
compile and behave exactly as before.

### Added

- **A mutation context on every write handler** (`MMCA.Common.Application`). `MutationContext` is a
  per-command side channel: a typed bag (`Set` / `TryGet` / `GetOrDefault` / `Contains`) for values a
  mutation derives while the aggregate is loaded, plus `SkipSave()`, the idempotent-no-op signal that
  finishes the command successfully with no save, no `LogMutated` and no `OnMutatedAsync`.
  `MutateEntityHandlerCore` threads one instance through `LoadAsync`, `MutateAsync`, `LogMutated` and
  `OnMutatedAsync` as a new overload of each, every one of which forwards to the context-free overload
  by default, so a handler that needs neither never sees it. `MutateAsync(entity, command, token)` is
  now `virtual` rather than `abstract` (a handler overrides exactly one of the two overloads;
  overriding neither throws at the call site), which is the one behavioral note for anyone writing a
  new handler.
- **`MutateEntityPayloadHandlerBase<TCommand, TEntity, TIdentifierType, TResultPayload>`**
  (`MMCA.Common.Application`). The third mutate flavor beside the bare-`Result` and refreshed-DTO
  ones, for a command whose response is a purpose-built envelope rather than the aggregate's DTO. The
  payload is unconstrained and the subclass builds it in
  `BuildResult(entity, command, context)`, reading both the mutated aggregate and whatever the
  mutation wrote into the context, so a pre-mutation value reaches the response without handler
  instance state. It is a sibling type rather than a fourth parameter on the DTO flavor, because
  generic types overload by arity alone.
- **Attempt-scope parity for mutations** (`MMCA.Common.Application`).
  `MutateCoreAsync(attemptUnitOfWork, command, token)` and its context-taking overload mirror the
  create workflow's `CreateCoreAsync`: a handler whose write can lose a unique-key race overrides
  `HandleAsync`, wraps the workflow in a retry loop and runs each attempt against a fresh DI scope's
  unit of work, instead of reimplementing load-stamp-mutate-save around the base.
- **`DeleteEntityHandler` is extensible** (`MMCA.Common.Application`). `HandleAsync` is `virtual`, and
  the workflow is split into `Includes`, `AsTracking`, `LoadAsync`, the Result-returning pre-delete
  hook `OnDeletingAsync` (a refusal stops the delete and the save), `LogDeleted`, `HandlerName` and a
  protected `UnitOfWork`. A subclass declares the child collections the aggregate's `Delete()` cascade
  has to see, or an invariant that spans more than the aggregate. With no `Includes` the handler
  issues the same bare by-id query it always has, so an existing consumer sees no change.
- **Verb-discriminated updates** (`MMCA.Common.Application`).
  `UpdateEntityCommand<TEntity, TUpdateRequest, TIdentifierType, TApplier>` derives from the
  three-parameter command and adds the applier type as a discriminator, so two verbs over one request
  DTO stay two commands with two appliers.
  `UpdateEntityHandler<TEntity, TEntityDTO, TIdentifierType, TUpdateRequest, TApplier>` resolves that
  exact applier, and `AddEntityUpdateVerb<...>()` registers one verb (handler plus validator bridge).
  The wire shape is unchanged: same route, same request DTO.
- **Commands that carry state beside the request** (`MMCA.Common.Application`).
  `UpdateEntityCommand<TEntity, TUpdateRequest, TIdentifierType>` is no longer sealed, so a module can
  derive a positional record adding a route-derived id, a server-decided flag or a second concurrency
  token while inheriting `Id`, `Request`, `RowVersion`, the `ICommandWithRequest` validator bridge and
  the cache prefix. `IEntityUpdateCommandApplier<TEntity, TUpdateRequest, TIdentifierType, TCommand>`
  is the applier that sees the whole command (and the mutation context),
  `UpdateEntityCommandHandler<TCommand, ...>` runs it on the shared workflow, and
  `AddEntityUpdate<TCommand, ...>()` registers the pair. Post-load, pre-mutate work such as
  `SetOriginalRowVersion` on a tracked child row needs no new hook: a subclass overrides `MutateAsync`,
  does its work against `UnitOfWork.GetRepository<...>()` and awaits `base.MutateAsync(...)`, which is
  now documented on `UpdateEntityHandler`.
- **`AddCommandRequestValidator<TCommand, TRequest>()`** (`MMCA.Common.Application`). The explicit
  form of the `ICommandWithRequest` bridge the module scan applies by reflection, for a command the
  scan cannot see because it is a closed generic constructed at registration time. `TryAdd`
  semantics, so an explicit `IValidator<TCommand>` still wins. The two new registration helpers call
  it for the commands they register.

## [1.171.0] - 2026-08-30

Two follow-ups to the 1.170.0 small-app floor: SQLite databases now migrate at startup like SQL
Server ones, and a tenant-less write answers with a client error instead of a 500.

### Changed

- **SQLite sources with a configured migrations assembly migrate at startup.** Migration targets
  are selected by the new `PhysicalDataSource.UsesMigrations` rule: SQL Server always, SQLite only
  when `SqliteMigrationsAssembly` is configured, Cosmos never. `MigrateAsync` and
  `HasPendingMigrationsAsync` share the rule, and database initialization no longer runs
  EnsureCreated ahead of a migrating SQLite source (EnsureCreated-then-Migrate would leave tables
  with no `__EFMigrationsHistory`). A SQLite source without a configured migrations assembly keeps
  the EnsureCreated behavior, and SQL Server single-database monoliths (no `DataSources` entry, so
  no configured assembly) are unchanged. A scaffolded `--database sqlite` application now requires
  `dotnet ef migrations add InitialCreate` before its first run.
- **`CrossTenantWriteException` maps to 400.** A write carrying no tenant, or a foreign tenant, on
  a tenancy-enabled host previously surfaced as an unmapped 500. `GlobalExceptionHandler` now
  answers a `ProblemDetails` 400 titled "Tenant write rejected" with a fixed generic detail; the
  exception's entity and tenant identifiers are logged at Warning and never echoed to the caller.

## [1.170.0] - 2026-08-29

The small-app floor release: the write-side generic CRUD twin, an outbox that resolves from the
messaging mode, SQLite as a runnable engine, and a one-reference metapackage. Existing SQL Server +
broker hosts see no behavior change; hosts on in-process messaging should read the outbox note below.

### Added

- **Generic write-side entity commands** (`MMCA.Common.Application`, `MMCA.Common.API`), completing
  the generic read side that `EntityControllerBase` has carried since ADR-034 (see ADR-099).
  `IEntityUpdateApplier<TEntity, TUpdateRequest, TIdentifierType>` is the per-aggregate contract that
  wraps guarded mutation methods; `UpdateEntityCommand` (an `ICommandWithRequest` +
  `ICacheInvalidating` record) and the generic `UpdateEntityHandler` ride the existing mutate
  workflow (repository via `IUnitOfWork`, `SetOriginalRowVersion` concurrency, events raised only by
  the aggregate). A concrete `CreateEntityHandler` closes `CreateEntityHandlerBase` for aggregates
  that need no hooks, and `AddEntityCrud<...>()` registers the create/update/delete handler set in one
  call. `CrudEntityControllerBase` derives from `AggregateRootEntityControllerBase` and adds an
  idempotent `PUT` with weak-ETag concurrency; it is a new base rather than a change to the shipped
  one, so existing controllers keep their generic arity.
- **`MMCA.Common` metapackage.** One `PackageReference` bundling the Core 6 (`Shared`, `Domain`,
  `Application`, `Infrastructure`, `API`, `Aspire`); UI, Testing, and MAUI packages stay explicit
  references where used (ADR-101). Package count is now 17.
- **SQLite as a runnable engine** (`MMCA.Common.Infrastructure`, `MMCA.Common.Aspire`).
  `DesignTimeDbContextHelper.CreateSqlite` for migrations tooling,
  `DataSourceEntrySettings.SqliteMigrationsAssembly`, and a SQLite health check registered by
  `AddInfrastructureHealthChecks` when a SQLite connection string is configured (new optional
  `requireDatabase` parameter; `requireSqlServer` semantics unchanged).
- **`MessageBus:EnableOutbox`** (`MMCA.Common.Infrastructure`). Nullable, resolved like
  `EnableInbox`: the outbox is on unless the provider is `InProcess`, and either default can be
  overridden explicitly. A broker provider with the outbox explicitly disabled fails at startup. A
  one-shot `OutboxDisabledNoticeService` log line states the resolved posture (ADR-100).

### Changed

- **Hosts on in-process messaging no longer run the durable outbox by default** (ADR-100). Events
  dispatch synchronously in process; the outbox tables remain mapped, so no migration churn. Set
  `MessageBus:EnableOutbox: true` to keep the previous durable behavior on an `InProcess` host.
- **A host starts with any configured database engine.** The `[Required]` annotation on
  `SQLServerConnectionString` is replaced by a validator that accepts a SQL Server, SQLite, or Cosmos
  connection string at the top level or on any `DataSources` entry; a host with no database
  connection anywhere still fails at startup with a message naming both configuration shapes.
- **Framework-owned persistence follows the configured engine** (ADR-018 revision). When a requested
  engine has no connection string anywhere, `DataSourceResolver` routes framework components
  (scheduler, outbox, audit trail, refresh sessions, transactional factory) to the sole configured
  engine, relational preferred. SQL-Server-only and mixed-engine hosts resolve exactly as before.
- **`IntegrationEventContractTestsBase` compares event members as an unordered set**
  (`MMCA.Common.Testing.Architecture`). Member order is not part of a JSON contract; existing
  alphabetical literals keep passing, and scaffolded literals no longer depend on name ordering.

### Fixed

- **Hosts without an Identity module failed every request** with an unresolvable
  `IPermissionRegistry` in the authorization decorator. `AddApplicationDecorators` now `TryAdd`s an
  internal fail-closed registry (permission-gated requests get `Forbidden`, with a one-time warning
  naming `AddAuthorizationPolicies()`); hosts that register a real registry keep it.
- **SQLite-only hosts opened SQL Server connections with empty connection strings** from the
  scheduled-job runner and the transactional factory; both symptoms of the engine routing fixed
  above.

## [1.169.0] - 2026-08-29

Follow-up to the 1.168.0 facade work: the two gaps the downstream migration surfaced. Interactive
snackbars get a contract of their own, and the shipped bUnit base registers the facades so consumer
component tests resolve them without a local workaround.

### Added

- **`IToastService.ShowAction`** (`MMCA.Common.UI`). Raises a toast carrying a button
  (`ShowAction(message, actionText, onAction, severity, requireInteraction)`): the "undo", "view it",
  "retry" shape the facade could not express, so consumers no longer have to drop back to MudBlazor's
  `ISnackbar` to keep an interactive snackbar. `requireInteraction: true` pins the toast open until
  the user dismisses it and renders it filled, the same emphasis convention `ShowPersistent` uses. The
  callback runs outside any render callback, so a caller whose work can fail guards it itself.
- **`AddCommonUiFacades()`** (`MMCA.Common.UI`). The toast/confirm-dialog facade registration pair as
  its own extension, so a test harness can register exactly those two over MudBlazor without the rest
  of the shared-UI surface. `AddUIShared` calls it, so host behaviour is unchanged; TryAdd means a
  host or test that pre-registers a substitute keeps it.

### Changed

- **BREAKING for implementors: `IToastService` gains a member.** `ShowAction` is a new interface
  method, so a type outside the framework that implements `IToastService` must add it. Per the
  [versioning policy](https://ivanball.github.io/docs/guides/common-VERSIONING.html) an
  implementor-breaking interface addition ships as a MINOR release; callers and the framework's own
  Mud-backed implementation are unaffected.
- **`BunitComponentTestBase` registers the UI facades** (`MMCA.Common.Testing.UI`). The shipped base
  now calls `AddCommonUiFacades()` right after `AddMudServices()`, so a consumer component test that
  renders a page injecting `IToastService` / `IAppDialogService` resolves them from the harness.
  Consumer test bases that added the pair locally can drop those registrations.
- **The localized-text fitness rule covers `ShowAction`** (`MMCA.Common.Testing.Architecture`): a
  literal first argument to it fails the gate like every other toast method (ADR-027).

## [1.168.0] - 2026-08-29

Adherence release: the patterns the framework asks consumers to follow are applied to the framework
itself. Entities get real identity equality, integration events get a stable wire name, the two
config-driven knobs that were still hard-coded (cache durations, gRPC circuit breaking) move into
settings, MudBlazor stops leaking through the UI helper surface, and two obsolete/dead surfaces are
deleted rather than carried another cycle.

### Added

- **`BaseEntity<TId>` identity equality.** `Equals`, `GetHashCode` and the `==`/`!=` operators compare
  by concrete type plus assigned `Id`, so the same row loaded through two contexts compares equal
  instead of answering a reference comparison. Transient instances (id still at the type default) are
  equal only to themselves, and the XML docs record the hash-changes-on-save caveat for
  database-generated keys.
- **`[EventName]` attribute.** Declares a stable outbox/inbox identity for an integration event, so
  renaming or moving the CLR type no longer orphans the messages already sitting in a queue or an
  outbox table.
- **`CacheSettings`** (config section `Cache`). Cache durations and the populate-lock timeout come
  from configuration instead of compiled-in constants, so an environment can tune them without a
  release.
- **`GrpcResilienceDefaults`.** Explicit circuit-breaker values for east-west gRPC, named in one
  place rather than inherited from the generic HTTP defaults.
- **`IToastService` / `IAppDialogService`** (`MMCA.Common.UI`). Thin MudBlazor facades, so component
  and helper code depends on the framework's own contracts and a head can substitute its own
  notification and dialog surface.
- **`UseMmcaMauiErrorHandling`** (`MMCA.Common.UI.Maui`). One global exception hook for a MAUI head,
  covering the unhandled-exception paths a hybrid app otherwise loses silently.
- **`EntityPropertySettersAreNonPublic` architecture rule** (`MMCA.Common.Testing.Architecture`),
  wired into `EntityConventionTestsBase`: every public property on a module domain entity must have
  no setter, an `init`-only setter, or a non-public one, so aggregate mutation goes through named
  domain methods (navigations included). Inherited by every consumer repo's entity-convention suite.
- **MassTransit in-memory harness test tier** for the integration-event consumers: a published event
  reaching its handler through a real bus, a handler failure surfacing as the `Fault<TEvent>` the
  fault consumer subscribes to, and a duplicate `MessageId` suppressed by the inbox. In-memory
  transport only, so the suite stays Docker-free.

### Changed

- **BREAKING: `IWriteRepository.Save` / `SaveChangesAsync` removed.** A repository no longer commits;
  the unit of work does. Call sites move to `IUnitOfWork.SaveChangesAsync`, which is what makes one
  command one transaction rather than one save per repository touched.
- **BREAKING: `GatewayDownstreamHealthCheckOptions.ProbeOverHttp2` removed.** The compatibility facade
  obsoleted in 1.167.0 is gone; use `ProbeVersion`, whose `Auto` default negotiates per downstream.
- **BREAKING: the `MMCA.Common.UI` helper surface takes the new facades.**
  `ResultUiExtensions.NotifyOnFailure` and `ListPageActions.DeleteWithConfirmationAsync` accept
  `IToastService` / `IAppDialogService` instead of MudBlazor's `ISnackbar` / `IDialogService`. Callers
  inject the facade (registered alongside the existing MudBlazor services) and pass that.
- **Logging decorator scopes carry `{ModuleName}`.** The command/query logging scope now names the
  owning module, so a log query can separate two modules' handlers without matching type names.

### Fixed

- **No-op `VersionOverride`s removed** from `MMCA.Common.Aspire.Hosting`: the three OpenTelemetry
  references restated the central pin they already resolved to, so the overrides only created a second
  place to forget.
- **CPM and analyzer conventions documented in place.** `Directory.Packages.props` records why
  transitive pinning is deliberately off (a security pin on a transitive package needs BOTH a
  `PackageVersion` entry AND a direct `PackageReference`), and `Directory.Build.props` records why the
  five analyzers ride in as per-project `PackageReference`s rather than CPM `GlobalPackageReference`s.

## [1.167.0] - 2026-08-29

Health-gating and E2E-reliability release closing the wave6 ledger items: Http2-only services become
health-gateable under Aspire, the gateway probe negotiates its protocol on its own, and the E2E
Blazor gate waits for real interactivity instead of a fixed settle.

### Added

- **`WithH2cHealthCheck` AppHost extension** (`MMCA.Common.Aspire.Hosting`). Health-gates a project
  resource whose cleartext endpoint is Kestrel Http2-only (h2c prior knowledge), which Aspire's
  stock `WithHttpHealthCheck` cannot probe (its HTTP/1.1 request answers `HTTP_1_1_REQUIRED`
  forever, wedging every `WaitFor` on the resource). The check GETs the health path over HTTP/2
  exact version against the existing endpoint: no service, Kestrel, or infra change needed. The
  XML docs record why surfacing the `HealthProbe:Port` HTTP/1.1 listener as an Aspire endpoint was
  rejected (explicit listeners override the `ASPNETCORE_URLS` binding locally and collide on the
  fixed cleartext port).
- **`GatewayDownstreamHealthCheckOptions.ProbeVersion`** (`DownstreamProbeVersion.Auto` default).
  The downstream probe tries HTTP/2 exact first, falls back to HTTP/1.1 within the same check on a
  protocol refusal, and latches the winner per downstream for process lifetime, so mixed
  `Http1AndHttp2` cleartext services no longer need a manual opt-out registration.
- **E2E interactivity marker.** `MmcaThemeProviders` stamps `data-mmca-interactive` on the document
  root at its first interactive render (re-stamped across enhanced navigations), giving tests a
  true hydration signal.

### Changed

- **`ProbeOverHttp2` is obsolete** (warning): it remains as a facade over `ProbeVersion`; delete
  per-downstream opt-out registrations and let `Auto` negotiate.
- **`WaitForBlazorAsync` waits for real interactivity.** After the Blazor runtime check it now
  waits up to 3s for the interactivity marker (then settles one animation frame); pages that never
  hydrate, or consumers on an older `MMCA.Common.UI`, fall back to the previous
  two-frames-plus-500ms settle, so nothing hangs and the prerendered-dead-control failure class is
  closed for every consumer suite at once.

## [1.166.0] - 2026-08-28

Hardening release from the 2026-08-28 Medium-literature audit (20 articles cross-checked against the
framework; report in the workspace `Reports/`).

### Added

- **`ContractImplementationTestsBase`.** New opt-in architecture fitness base asserting every class
  implementing a `[ServiceContract]` interface is non-public, so module encapsulation is a build
  gate rather than a convention; `AllowedPublicImplementations` is the escape hatch.
- **`CHANGELOG.md` ships in every package**, and the pack metadata gains `Copyright` and
  `PackageReleaseNotes`.

### Changed

- **BREAKING: write repositories are aggregate-root-only.** `IRepository` and `IWriteRepository`
  (with `EFRepository` and its decorator) are constrained to `AuditableAggregateRootEntity`,
  matching the constraint `IUnitOfWork.GetRepository` always had; the read-side surfaces
  (`IReadRepository`, `IEntityReader`, `IEntityQuerier`) still accept any `AuditableBaseEntity`.
  Code resolving a write repository for a child entity (including test doubles constrained to
  `AuditableBaseEntity`) must widen to the aggregate-root constraint or move to the read side.
- **All registered validators run.** `ValidatingCommandDecorator` and `ValidatingQueryDecorator`
  previously ran only the first `IValidator<T>` DI happened to yield; they now run every registered
  validator and union the failures, so a command carrying both a shared and a specific validator is
  checked by both.
- **Tenant index matches the composed filter.** For entities that are both `ITenantEntity` and
  soft-deletable, the auto-created index is now composite (`TenantId`, `IsDeleted`), matching the
  AND-composed query filters; tenant-only entities keep the single-column index. No consumer ships
  tenant entities yet, so no migration is expected downstream.

### Fixed

- **`MessageBusSettings.EndpointPrefix` is actually applied.** The configured value now prefixes
  kebab-case endpoint names (namespacing queues per service on a shared broker); previously it only
  toggled kebab-casing and the value itself was discarded.

## [1.165.0] - 2026-08-28

Wave 6 extraction release: framework surface hoisted from duplicated ADC/Store/Helpdesk code
(Tier 1 items 6.1-6.7 and Tier 2 items 6.8-6.15 of the extraction plan), all additive and opt-in.

### Added

- **Read-scoping hook on `EntityControllerBase`.** An async `GetReadSpecificationAsync` hook is
  honored by all five read actions (both `GetAll` overloads, lookup, `GetById`, and export), closing
  the unscoped-export hazard class at the base; the sync `GetExportSpecification` folds in as the
  default, and `SetConcurrencyETag` widens to `protected`.
- **CRUD handler bases.** `CreateEntityHandlerBase`, `MutateEntityHandlerCore`/`Base` (Result and
  DTO shapes, with the load / NotFound / `SetOriginalRowVersion` / mutate / save sequence and
  EntityId/RowVersion/include/log extension points), and `AddChildEntityHandlerBase` /
  `RemoveChildEntityHandlerBase` join `DeleteEntityHandler`, collapsing roughly fifty near-identical
  per-app handlers.
- **Persistence read surface.** `IEntityQuerier` gains `FirstOrDefaultAsync` (predicate and
  specification overloads), `CountByAsync`/`SumByAsync` grouped aggregates, and
  `FindIncludingDeletedAsync` (active vs soft-deleted split), with `EFReadRepository` and decorator
  implementations, retiring the materialize-all-then-filter call sites.
- **Unique-constraint violation detection.** `IUniqueConstraintViolationDetector` with a
  `SqlServerUniqueConstraintViolationDetector` (SQL 2601/2627 plus message fallback), registered by
  `AddInfrastructure`.
- **Aggregate child helpers.** `RemoveChildOrNotFound` and `RestoreChild` beside the shipped
  `GetChildOrNotFound`, plus `IReactivatable`.
- **`ScopedIntegrationEventHandlerBase<TEvent>`.** The `CreateAsyncScope` preamble and
  log-and-rethrow envelope (cancellation excluded, mirroring `SafeDomainEventHandler`) hoisted from
  eighteen per-app integration-event handlers; `HandleScopedAsync` and `LogHandlerFailure` are the
  extension points.
- **gRPC Result trailer decoder.** `Metadata.ToErrors()` and `RpcException.ToResult`/`ToResult<T>`,
  the exact inverse of the shipped `ToRpcException` and living in the same file so encoder and
  decoder cannot drift; unstructured RPC failures synthesize a `Grpc.{StatusCode}` error.
- **Opt-in service-host extensions.** `AddCommonSerilog(logFilePath)` (the identical Serilog
  bootstrap all seven consumer hosts repeat, with a post-defaults configure hook) and
  `AddModuleHost()` (settings binding, `ModuleLoader` construction, and a `RegisterModules` method
  group the host drops into its own pipeline call), leaving `Program.cs` orchestration and the
  `AddApplicationDecorators()`-last ordering host-owned.
- **Push device-token providers (MMCA.Common.UI.Maui).** Both halves: the FCM provider plus
  `MauiFirebaseMessagingService` (Android) and the APNs provider plus a now-public
  `ApnsTokenBridge` (iOS/MacCatalyst), registered by `AddMauiPushDeviceTokenProvider()`; AppDelegate
  hooks, manifest/plist entries, and credentials stay app-side. Adds the `Xamarin.Firebase.Messaging`
  dependency to the MAUI package.
- **`MmcaGatewayHardeningTestsBase<TEntryPoint>`** (MMCA.Common.Testing) with eight gateway gates
  (rate-limit window, named policy, bypass paths, correlation-id generation and echo, per-downstream
  readiness, active health-check probes, forwarded-client-IP partitioning) over abstract
  route/limit/downstream inputs, plus a public `RecordingHttpForwarder` and
  `NeutralizeGlobalRateLimiter()`. Adds the `Yarp.ReverseProxy` dependency to Testing.
- **UI component bundle.** `ListPageActions`, `ListNoRecordsContent`, `InfiniteScrollSentinel`,
  `SharePageButton` + `QrCodeButton` over a new `IPublicLinkBuilder` (browser implementation
  registered by default, MAUI implementation via `AddCommonMauiPublicLinkBuilder()`),
  `ApiFileDownloadButton` (the generalized download-from-endpoint button), and
  `OfflineFirstPageSnapshot<TItem>` over `ILocalCacheStore`, all localized en+es and independently
  consumable.
- **Testing bundle.** bUnit `ConfigureDataGridListPageHost()` (encoding the load-bearing
  `SetRendererInfo`-last ordering), `ErrorSummaryMessages()`, a now-public
  `AddDeviceCapabilityDefaults()`, and E2E `SearchAndWaitForRowAsync` (deterministic waits, no
  sleeps), `ConfirmDeleteAsync`, `WaitForGridToSettleAsync`, `MeasureWebVitalsAsync`, and
  `PseudoLocalizationTestsBase`.
- **Leaf additions.** `CommonInvariants` bundle 2 (enum-defined, end-not-before-start, string
  length windows, optional max length, time-zone validity, URL well-formedness, count/uniqueness/
  flag/nullable-int members), an optional `errorCode` parameter on every `CommonValidationRules`
  rule class plus `AbsoluteUrlRules` (rejecting `javascript:`/`data:`/relative values),
  `RequireUserId()` on `ICurrentUserService`, `NotificationScopeKey` (formatter and validation
  pattern in one place, now the `ChannelKeyPattern` default), `AddMailDev()` for AppHosts,
  `GetRequiredJwtAuthority()`, `EvictTagsAsync`/`TryEvictTagsAsync` output-cache params extensions
  (throwing and best-effort variants), and `UseCommonForwardedHeaders()` reachable from the Gateway
  package.

### Changed

- **`CommonValidationRules` rule-class constructors** gained a trailing optional `errorCode`
  parameter: source-compatible for subclasses, binary-breaking for the superseded signatures (the
  PublicAPI log records the removals). Omitting the parameter is behavior-identical to before.

### Dependencies

- Aspire.Hosting (and `.Azure.CosmosDB` / `.RabbitMQ` / `.SqlServer`) 13.5.2 -> 13.5.3.
- `Grpc.AspNetCore` 2.80.0 -> 2.83.0, aligning it with the `Grpc.AspNetCore.Server.Reflection` and
  `Grpc.Net.ClientFactory` pins.
- `Microsoft.FeatureManagement` + `.AspNetCore` 4.6.0 -> 4.7.0. 4.7.0 drops the transitive
  `Microsoft.Bcl.TimeProvider` shim, so the four hosted services that called its
  `TimeProvider.Delay` extension now call the BCL `Task.Delay(TimeSpan, TimeProvider,
  CancellationToken)` overload directly (identical semantics).
- `Meziantou.Analyzer` 3.0.177 -> 3.0.190; new rule MA0219 (language attribute on XML `<c>`
  elements) is set to `none` in the shared `.editorconfig` baseline.
- `Scalar.AspNetCore` 2.17.1 -> 2.17.2.
- `MessagePack` 2.5.302 -> 3.1.8. No framework code uses it: the pin exists to lift the transitive
  the SignalR Redis backplane and Aspire.Hosting pull, and both constrain it with a lower bound
  only.
- `StackExchange.Redis` 2.13.17 -> 3.1.31 (major). The Redis Testcontainers tier passes against a
  real server; two test doubles moved off constructors 3.x deprecates.
- `Microsoft.Data.SqlClient` 6.1.6 -> 7.0.2 in the test-tier pin (`MMCA.Common.Testing` is its only
  direct reference; other projects keep resolving the 6.1.6 EF Core SqlServer floors).
- MAUI train (`MMCA.Common.UI.Maui`): `Microsoft.Maui.Controls` +
  `Microsoft.AspNetCore.Components.WebView.Maui` 10.0.80 -> 10.0.100, `CommunityToolkit.Maui`
  14.2.2 -> 15.0.1 (major), `ZXing.Net.Maui.Controls` 0.10.3 -> 0.10.4. All four TFMs build clean.
- Held at their current versions: `MassTransit` (+ `.RabbitMQ` / `.Azure.ServiceBus.Core`) 8.5.10,
  the standing v8 ceiling because v9 requires a commercial license; `SixLabors.ImageSharp` 3.1.12,
  whose Six Labors Split License is treated as within the same commercial exclusion;
  `Microsoft.OpenApi` 2.12.2, because `Microsoft.AspNetCore.OpenApi` 10.0.11 and
  `Asp.Versioning.OpenApi` 10.2.3 both constrain it below 3.0.0 (NU1608); and the two Android
  bindings `Xamarin.Firebase.Messaging` 124.1.2 and `Xamarin.AndroidX.Biometric` 1.1.0.30, whose
  newer builds resolve AndroidX past the `.Ktx` packages' upper bounds (NU1608).

## [1.164.1] - 2026-08-27

### Fixed

- **`MobileInfiniteScrollList` regained its inline retry affordance under the Result contract.**
  v1.164.0 converted the UI HTTP services to Result-returning, but the infinite-scroll component
  still signaled failure by caught exception, so converted consumers lost the inline
  failure-message-plus-Retry rendering. A new `FetchPageResult` parameter accepts the
  Result-returning fetcher and renders the failure's localized message with a Retry button; the
  tuple-returning `FetchPage` parameter keeps working but is `[Obsolete]`. Exactly one fetcher must
  be supplied (misconfiguration throws at initialization), and cancellation semantics are unchanged.

## [1.164.0] - 2026-08-27

### Added

- **Per-device session management surface.** Issued and rotated refresh sessions now stamp a `sid`
  claim (additive), and `AuthControllerBase` gains `GET my-sessions` plus `POST revoke/{sessionId}`
  backed by `RefreshSessionSummaryResponse`, an ownership-scoped `FindByIdAsync`, and
  `GetSessionsAsync`/`RevokeSessionByIdAsync` on the authentication service, so a user can review
  devices and sign out one of them or everywhere.
- **Refresh-session retention sweep.** `RefreshSessionCleanupService` purges session rows that
  stopped being usable more than `RefreshSessions:RetentionDays` (default 30) ago, gated on
  `RefreshSessions:Enabled`, with the reuse-detection-window trade-off documented on the setting.
- **Shared severity ranking and ProblemDetails reader.** `ErrorTypeSeverity` is hoisted so the HTTP
  and gRPC edges classify multi-error aggregates identically (gRPC no longer picks the first error
  positionally), and `ProblemDetailsResultReader` converts RFC 9457 payloads back into typed `Error`
  lists (`ErrorType` preserved on the MMCA error-array shape; the lossy plain-400 reverse mapping is
  documented), forming the foundation of the UI Result conversion below.
- **Result ergonomics for pages.** `ResultUiExtensions` (`TryGetValue`, `OnFailureSetError`,
  `NotifyOnFailure` with localization, deduplication, and severity ordering, plus `HasErrorType`
  helpers) and a shared deduplicating `ErrorSummary` alert component, so consumer pages convert to
  the Result contract uniformly.
- **Sessions page.** A `/profile/sessions` page (device list with a current-device badge, per-row
  revoke, sign-out-everywhere), its nav entry, and 23 localized keys in en and es, axe-scanned in
  the gallery; `NavigationFlow.md` updated.

### Changed

- **BREAKING: every `MMCA.Common.UI` HTTP-typed-client service returns `Result`/`Result<T>` instead
  of throwing `DomainInvariantViolationException`.** `ServiceExceptionHelper` is deleted;
  `HttpResultExecutor` and `ProblemDetailsResultReader` preserve `ErrorType` end to end, and
  `OperationCanceledException` still propagates for cancellation.
  `EntityServiceBase`/`ChildEntityServiceBase`/`AuthUIService` and the notification services are all
  converted (41 removals and 98 additions in the public API surface). Consumer pages migrate onto
  `ResultUiExtensions`/`ErrorSummary` rather than try/catch blocks.

## [1.163.0] - 2026-08-26

### Added

- **`MMCA.Common.Gateway` package.** The YARP gateway pieces both consumers hand-rolled now ship as
  a package: cluster profile defaults, passive health-check defaults, a route/cluster trace-header
  transform, and per-route rate-limiter policies. Route tables stay app-side by design.
- **Soft-delete completion.** `DeletedOn`/`DeletedBy` audit columns are stamped on the `IsDeleted`
  transition, the unique-index convention now appends the soft-delete clause to hand-authored
  filters instead of skipping them, a `DeleteChildren` cascade helper joins the aggregate base, and
  `SoftDeleteEnforcementTestsBase` adds a hard-delete fitness rule.
- **Result combinators.** `Bind`/`Tap`/`Ensure`/`MatchAsync` with `Task<Result<T>>` overloads and
  implicit conversions, so handler and service code chains Results without ceremony.
- **MudForm validation bridge.** An `IModelValidator`/DataAnnotations adapter for MudBlazor forms
  with localization support (`NotificationSend` converted as the reference page), and the duplicate
  `ApiEndpoint` guard removed.
- **Three new fitness rules** in `MMCA.Common.Testing.Architecture`:
  `DomainEventHandlerSaveTestsBase` (transitive IL call-graph walk proving no `SaveChanges` under an
  `IDomainEventHandler`), `ErrorCatalogTestsBase` (error-code uniqueness plus module prefix), and
  `SortableColumnConventionTestsBase` (no `TemplateColumn` with `Sortable="true"`).

### Changed

- **BREAKING: multi-device refresh sessions.** `IAuthUser` loses
  `RefreshToken`/`RefreshTokenExpiry`/`UpdateRefreshToken`/`RevokeRefreshToken`; refresh tokens now
  live in a per-user `RefreshSessions` table, SHA-256 hashed at rest, with rotation chains
  (`ReplacedByTokenHash`), reuse-detection family revocation, per-device or all-device sign-out, a
  configurable session cap, and IP/UserAgent capture. Mapping is opt-in via
  `ApplyRefreshSessionConfiguration` on the Identity context only. `AuthenticationServiceBase`
  signatures gain `ipAddress`/`userAgent`, the duplicate `user_id` claim is dropped in favor of
  `sub` only (`ClaimsPrincipalExtensions` and `AuthClaimTypes.Subject` added), and RS256 tokens now
  emit the JWKS `kid` header.
- **`ErrorType.Unexpected` maps to 500 on both edges**, and HTTP/gRPC status selection is
  severity-ranked instead of first-error-positional, so a validation error can no longer mask an
  unexpected failure in an aggregate Result.
- **h2c health probes default to HTTP/2 exact** on gateway cluster destinations, with a
  per-downstream opt-out for mixed-protocol services.

### Fixed

- **Mobile nav Logout row clearance.** The `.nav-auth-section` pinned at the bottom of the mobile
  menu had no bottom inset, so the Logout row could land under the bottom browser bar or home
  indicator; it now adds 0.75rem plus `env(safe-area-inset-bottom)`.

### Dependencies

- AngleSharp and 16 other package bumps, plus the analyzers group (3 updates).

## [1.162.0] - 2026-08-24

### Fixed

- **Stale body scroll-lock when the mobile nav closes via a link or the backdrop.** `closeMenu()` in
  the shared `navmenu.js` dispatched a non-bubbling synthetic `change` event, so the document-level
  listener that clears `document.body.style.overflow` never fired: the menu closed visually but the
  page stayed unscrollable. The MAUI `BlazorWebView` head uses the interactive router (no
  `enhancedload` re-sync), so the stale lock persisted until app restart. The synthetic event now
  bubbles (also restoring `aria-expanded`), and `closeMenu()` calls `syncOverflow()` directly.
- **`MauiGeolocationService` rejected Android approximate-only location grants.** MAUI's composite
  `LocationWhenInUse` permission reports a coarse-granted/fine-denied state (Android 12+
  "Approximate") as `Denied` on check and `Restricted` on request, and the service accepted only
  `Granted`, so approximate users silently lost location-based features. The service now treats
  `Granted` or `Restricted` as success on both the check and request paths, and a new Android-only
  coarse-location probe detects an existing approximate grant up front so those users are not
  re-prompted with the precise-upgrade dialog on every read.

## [1.161.0] - 2026-08-23

### Added

- **Sign in with Apple external login provider.** Apple joins Google and GitHub in the external
  OAuth pipeline, gated on `OAuth:Apple:ClientId` like the other providers. Apple issues no static
  client secret, so the handler mints a short-lived ES256 client-secret JWT from
  `OAuth:Apple:TeamId`, `OAuth:Apple:KeyId`, and `OAuth:Apple:PrivateKeyPem` (the .p8 content); the
  middleware handles Apple's cross-site form-post callback at `/auth/callback/apple`, and the
  single-use-code exchange flow is unchanged, so tokens never ride a redirect URL.
  `OAuthControllerBase` gains the `GET auth/oauth/apple` challenge endpoint, `IOAuthUISettings`
  gains `AppleEnabled` (default false, so existing consumers are untouched), and the shared login
  page renders the Apple button first among the social buttons (App Store guideline 4.8 requires an
  equivalent privacy-preserving option wherever third-party login is offered, and the Apple HIG asks
  for at least equal prominence), localized in en and es.

## [1.160.0] - 2026-08-22

### Added

- **Forgot-password / reset-password vertical (opt-in).** A complete account-recovery flow ships
  across the stack: `ForgotPasswordRequest` / `ResetPasswordRequest` wire contracts with framework
  validators, `ForgotPasswordHandlerBase<TUser, TCommand>` and
  `ResetPasswordHandlerBase<TUser, TCommand>` mirroring the ChangePassword vertical,
  `IPasswordResetTokenService` backed by `ICacheService` (256-bit single-use tokens hashed at rest,
  configurable TTL, bounded validation attempts, per-email request throttling via the new
  `PasswordReset` settings section), and `PasswordResetAuthControllerBase<TForgot, TReset>` exposing
  `POST forgot-password` (always 202 for well-formed input, so responses never disclose whether an
  account exists) and `POST reset-password`, both anonymous, idempotent, and behind the `auth-ip`
  rate-limit policy. The reset email is composed by overridable `ComposeSubject` / `ComposeBody` /
  `ComposeResetLink` hooks and carries both a prefilled reset link and the raw token for manual
  entry; with no `PasswordReset:ResetUrl` configured the email degrades to token-only. Apps opt in
  with two thin command records, two handler subclasses, and a sealed controller routed at `Auth`.
- **Shared password-recovery UI and E2E coverage.** `MMCA.Common.UI` adds `/forgot-password` and
  `/reset-password` pages (query-string prefill, existing password-complexity validation, an
  always-confirm submit state on the request page) plus a "Forgot password?" link on the login page,
  with new `IAuthUIService.RequestPasswordResetAsync` / `ResetPasswordAsync` members and localized
  en/es resources. `MMCA.Common.Testing.E2E` adds `ForgotPasswordPage` / `ResetPasswordPage` page
  objects and `PasswordResetTestsBase` (navigation, anti-enumeration confirmation, client
  validation, and WCAG 2.1 AA scans of both pages).

## [1.159.0] - 2026-08-22

### Added

- **Event upcaster registration extension point (ADR-010, ADR-090).** The consumer-side half of the
  event-schema versioning policy now ships as a mechanism instead of a convention:
  `IEventUpcaster<TSource, TTarget>` (a pure payload mapping from a retired integration-event
  contract to its successor) plus `services.AddEventUpcaster<TSource, TTarget, TUpcaster>()`.
  Upcasters accumulate across modules into an `IEventUpcasterRegistry` that follows chains
  (V1 to V2 to V3) to the terminal contract and preserves `MessageId` and `DateOccurred` across
  every hop, so inbox deduplication keys stay stable by construction. Misconfiguration (duplicate
  source types, self-maps, cycles) fails at host startup with the offending types named, not on the
  first message.
- **Both delivery paths honor registered upcasters.** In-process (monolith) dispatch upcasts inside
  `DomainEventDispatcher` before selecting integration handlers, which also covers outbox rows
  written before an upgrade. Broker hosts drain a retired contract with
  `RegisterUpcastedIntegrationEventConsumer<TOld>()`, a dedicated MassTransit consumer that upcasts
  to the terminal type and dispatches its handlers; handlers are written once, against the newest
  contract only.
- **Two new event fitness functions** in `MMCA.Common.Testing.Architecture`, inherited automatically
  by every `EventConventionTestsBase` subclass: no two upcasters may share a source type, and an
  upcaster's target must declare a strictly higher `SchemaVersion` than its source.
- **`AnonymousEndpointTestsBase` (MMCA.Common.Testing.Architecture).** An anonymous-endpoint fitness
  function: it scans MVC controllers and routable Blazor components by full-name reflection (no
  ASP.NET reference) and fails on any `[AllowAnonymous]` missing from the subclass's explicit
  allow-list, on a stale allow-list entry, and on an empty scan. Subclass it per repo with
  `TargetAssemblies`, `AllowedAnonymousEndpoints`, and a `MinimumScannedTypes` floor. Minimal-API
  `.AllowAnonymous()` is endpoint metadata rather than an attribute, so it stays out of reach and is
  documented as such.
- **Executable security invariants for the auth and CORS registrations.** New tests run the real
  registration code and assert the produced options: RS256 stays pinned on the forwarded JWT bearer
  path, the permissive CORS policy never supports credentials, the credentialed policy never widens
  to any origin (service host and gateway alike) and fails closed with no configured origins, and
  `RsaJwksProvider` publishes only public RSA parameters even when handed a private-key PEM.

### Changed

- **BREAKING: `AddForwardedJwtBearer` is secure by default and takes the host's configuration and
  environment.** The signature moves from
  `AddForwardedJwtBearer(string authority, string audience, bool requireHttpsMetadata = false)` to
  `AddForwardedJwtBearer(string authority, string audience, IConfiguration configuration, IHostEnvironment environment, bool? requireHttpsMetadata = null)`.
  `RequireHttpsMetadata` now resolves in three steps: the explicit argument, then the new
  `WebApplicationBuilderExtensions.RequireHttpsMetadataConfigKey`
  (`Authentication:JwtBearer:RequireHttpsMetadata`), then `true` everywhere except Development. The
  old bare `false` default applied in production too. Call sites pass
  `builder.Configuration, builder.Environment`; a deployment whose authority is genuinely plain HTTP
  (an internal-ingress `h2c` service URL) sets the configuration key to `false`, which is honored and
  logged once at startup by an internal `IStartupFilter` naming the key. The previous
  `(string authority, string audience, bool requireHttpsMetadata = false)` overload is retained for
  one release as a transitional bridge with its historical behavior, so consumer `main` branches
  keep compiling against framework source while their call sites migrate; it is deleted once the
  consumer sweep lands.

## [1.158.0] - 2026-08-21

### Added

- **Named-step HTTP edge pipeline.** The shared middleware pipeline that `UseCommonMiddlewarePipeline()`
  applies is now modeled as an ordered list of named steps rather than a straight-line method body:
  `MiddlewarePipelineStepNames` (one constant per step), `MiddlewarePipelineStep` (name plus the
  delegate that registers the step), and `MiddlewarePipelineBuilder` (`CreateDefault()` seeding the
  18 framework steps, `StepNames`, and `Build()`). The order is data now, so it can be asserted
  without starting a host.
- **Scoped extension point: `UseCommonMiddlewarePipeline(Action<MiddlewarePipelineBuilder> configure)`.**
  A host can `InsertBefore`, `InsertAfter`, `Replace`, or `Remove` steps by name instead of the
  previous all-or-nothing choice between the framework pipeline and composing its own. Unknown
  anchors and duplicate step names throw `ArgumentException`.
- **Startup-validated invariants.** `Build()` runs before any step is applied and throws
  `InvalidOperationException` naming the violated invariant when the pre-forwarded capture is not
  immediately before `UseForwardedHeaders`, authentication is not immediately before tenant
  resolution, authentication does not precede the rate limiter (ADR-019), or forwarded headers do
  not precede the HTTPS redirect. An invariant binds only when both of the steps it names are
  present, so removing a whole capability stays legal.
- **`MiddlewarePipelineOrderTestsBase` (MMCA.Common.Testing).** The edge counterpart of
  `DecoratorPipelineOrderTestsBase`: two facts that assert the step order matches the documented one
  and that `Build()` raises no invariant. Subclass it with an empty body for a host on the default
  pipeline, or override `Configure`/`ExpectedStepNames` for a host that customizes it. No database
  and no `WebApplication`, so it runs in the fast unit tier.

### Changed

- `MMCA.Common.Testing` now depends on `MMCA.Common.API` (same lockstep version) so the new test base
  can see the pipeline builder.
- `UseCommonMiddlewarePipeline()` keeps its signature and behavior exactly: it delegates to the
  validated default step list, applying the same middleware in the same order under the same
  conditionals. No host code changes.

> **Consumer versions skipped deliberately (historical note).** MMCA.ADC, MMCA.Store and MMCA.Helpdesk went from
> 1.127.0 straight to 1.131.0 on 2026-07-28. They never pinned 1.128.0, 1.129.0 or 1.130.0, and that
> is intentional, not drift. 1.128.0 was distribution-only (assemblies byte-identical to 1.127.0), so
> sweeping it alone would have cost two production deploys for no behavioural change; 1.129.0 and
> 1.130.0 were superseded within the same day by 1.131.0, which the consumers took in one pass per
> ADR-016. An audit that reports the consumers as "several versions behind" for that window is
> reading history, not a gap.

## [1.157.0] - 2026-08-20

Notification UI reliability release: fixes the bell badge and Notifications inbox going stale
until logout/login in consumer apps.

### Fixed

- **Poller slot leak in `NotificationState`.** `TryRegisterPoller` incremented an internal counter
  unconditionally while `NotificationBell` unregistered only when it held the active slot. Hosts
  rendering more than one bell (desktop app bar plus mobile nav) inside an `AuthorizeView` leaked
  one registration per authentication-state rebuild (every access-token refresh), after which no
  bell instance polled again for the life of the circuit: no initial unread-count fetch, no 30s
  poll, no navigation refresh. The counter is replaced by an owner-based slot; the bell always
  unregisters on dispose, and surviving bells take over polling when the active one goes away.
- **401 on the unread-count fetch no longer collapses to 0.** `NotificationInboxService` mapped any
  non-success response, including 401 from a stale in-memory token, to a count of 0, wiping the
  badge right after a push had optimistically incremented it. Both inbox reads now force one token
  refresh and replay on 401; a still-failing count reports unknown (`null`) and the bell leaves the
  badge untouched instead of zeroing it.
- **Push-triggered inbox refresh no longer dropped.** `NotificationInbox` discarded a refresh
  request that arrived while a load was in flight; it now queues exactly one trailing reload.

### Changed

- `INotificationInboxUIService.GetUnreadCountAsync` returns `Task<int?>` (null means unknown, never
  0 on failure). `NotificationState.TryRegisterPoller`/`UnregisterPoller` now take the registering
  owner, and `NotificationState` raises `OnPollerSlotFreed` when the polling slot frees up.
  `AuthenticatedServiceBase` gains a protected `CreateClientWithToken(string)` helper; its
  constructor is unchanged.

## [1.156.0] - 2026-08-20

Maintenance only: synced the out-of-slnx `MMCA.Common.UI.Maui` `packages.lock.json` with the
current pins. No source or behavioral changes.

## [1.155.0] - 2026-08-19

Shared UI theme refresh: the framework look-and-feel that every consumer app inherits is
professionalized in one pass. Purely visual; no palette color values, layout structure, or
component semantics changed.

### Added

- **Self-hosted Inter web font.** The theme has always named Inter first in its font stack but no
  web font was ever loaded, so every app silently fell back to Segoe UI/Arial. `MMCA.Common.UI`
  now vendors the Inter woff2 files (weights 400, 500, 600, 700, 800, latin subset, SIL OFL 1.1
  notice included) under `wwwroot/fonts/` and declares them with `font-display: swap`. Served
  same-origin via `_content/MMCA.Common.UI/`, so the existing `font-src 'self'` CSP already
  permits them. Consumers can preload the critical weights from their host page.
- **Theme override extension point.** `MmcaThemeProviders` gains an optional `Theme` parameter
  (defaults to `MMCATheme.Instance`), so an app can supply a derived `MudTheme` without forking
  the providers component. Non-breaking.
- **Brand logo slot.** `LayoutSettings.BrandLogoUrl` (default empty); when set, `NavMenu` renders
  a decorative logo image beside the brand text.
- **Section primitives** in `app.css`: `.mmca-eyebrow` (uppercase, letter-spaced section label),
  `.mmca-stat-tile` (large numeral + small label stat block), and refined
  `.mmca-section-heading` spacing. New `--mmca-primary-light` token mirrors
  `BrandColors.PrimaryLight` (`BrandColorTokenTests` extended to pin the sync).

### Changed

- **Typography scale** (`MMCATheme`): h1/h2 weight 800 and h3/h4 weight 700 with slightly
  negative tracking and tightened line heights; h5/h6 weight 600; buttons move to sentence case
  (`TextTransform: none`) at weight 600. Consumer-visible on purpose: apps whose styling assumed
  uppercase button labels should spot-check at pin-bump time. All hand-tuned palette contrast
  fixes are untouched and no palette color value changed.
- **Layout chrome polish**: hairline borders on the app bar and sidebar (rendered as box-shadows
  so layout metrics are byte-identical), a rounded active-item pill with brand accent bar in the
  nav, refined nav section-label, user-identity, and footer typography. The structural
  sticky-sidebar rules, hamburger mechanism, and notification-badge positioning are untouched.
- **Shared primitive polish**: `EmptyState`, `PageHeader`, `PageLoadingState`, and
  `PageErrorState` get spacing/typography refinements; semantics, ARIA roles, and `PageHeader`'s
  `h1` emission are unchanged. Markup snapshot baselines regenerated.

## [1.154.0] - 2026-08-18

The Section B application-wave framework halves: a gateway edge kit, an idempotency-intent
convention gate, an HTTP ETag/If-Match concurrency surface, cross-service output-cache eviction,
a best-effort dispatch helper, and a proto contract gate. Consumers move from 1.152.0 directly to
this version; 1.153.0 was never pinned by any consumer (same deliberate-skip pattern as
1.128-1.130, recorded under Unreleased).

### Added

- **Gateway edge kit (ADR-088).** New `MMCA.Common.Aspire` Gateway namespace for YARP hosts that
  reference only the Aspire package: a context-free correlation middleware (ensures and echoes
  `X-Correlation-ID`, so downstream services' `CorrelationIdMiddleware` joins one trace),
  `AddGatewayRateLimiting` (per-client-IP fixed window that includes anonymous traffic, chained
  with a global concurrency cap; `GatewayRateLimiting` settings section with configurable bypass
  prefixes; health endpoints and `/.well-known` always bypassed; unknown IP fails open; in-memory
  per replica by design), and `AddGatewayDownstreamHealthChecks` (per-service `/alive` probes on
  the Ready tag so `/health/ready` reflects downstream reachability while the ACA `/alive` probe
  stays process-local).
- **Idempotency-intent convention (ADR-017 revision).** `[NonIdempotent(justification)]` marks a
  POST as deliberately outside the idempotency-key contract, and the new
  `IdempotencyConventionTestsBase` fitness base fails any `[HttpPost]` action declaring neither
  `[Idempotent]` nor `[NonIdempotent]` (inherit-aware). `AuthControllerBase.Register` is now
  `[Idempotent]`; Login/Refresh/Revoke and OAuth exchange are declared `[NonIdempotent]` because
  token issuance must never be replay-cached. The idempotency filter's no-op-without-a-key
  behavior is pinned by tests, so adopting `[Idempotent]` broadly is client-compatible.
- **ETag / If-Match concurrency surface (ADR-035 revision).** `EntityControllerBase.GetByIdAsync`
  emits a weak ETag from the DTO's RowVersion; the new `[SupportsIfMatch]` action filter parses
  `If-Match` into request models implementing the existing `IConcurrencyAware` contract (body
  value wins when both are present) and rewrites header-sourced concurrency conflicts to 412
  Precondition Failed while body-sourced round-trips keep their 409 semantics.
- **Cross-service output-cache eviction (ADR-026/077 revision).** `OutputCacheEvictionRequested`
  (the framework's first shipped integration event; frozen wire shape) plus an API-side handler
  that evicts each tag best-effort (`cache.eviction.failed` on the new `MMCA.Common.OutputCache`
  meter) and `RegisterOutputCacheEvictionConsumer` / `AddOutputCacheEvictionHandler` wiring, so a
  mutation in one service can evict another service's output cache through the standard outbox,
  broker and inbox path.
- **Best-effort dispatch helper.** `BestEffort.ExecuteAsync(operation, logger, action, ct)` wraps
  fire-and-forget side effects: non-cancellation failures are logged once and counted
  (`besteffort.dispatch.failed`, tag `operation`, new `MMCA.Common.BestEffort` meter);
  cancellation always propagates. Replaces hand-rolled catch-all blocks at call sites.
- **Proto contract gate.** `ProtoContractTestsBase` pins a repo's `.proto` surface (package,
  services, rpcs, messages, fields with numbers, enums) against a frozen list, closing the gap
  where only integration-event payloads were contract-gated. Consumer-facing; the framework
  ships no protos.

## [1.153.0] - 2026-08-18

The Section A architecture wave: broker poison-message handling, pipeline authorization and
timeout policies, specification-driven queries with projection pushdown, keyset pagination, and
three new build gates (namespace cycles, CancellationToken declarations, public API surface).
ADRs 085-087 plus revisions to 009/014/015/019/021/031/048/054/055 document the wave.

### Added

- **Second-level broker redelivery and fault observability (ADR-087).** A message that exhausts
  `UseMessageRetry` is now rescheduled per `MessageBus:RedeliveryIntervalsSeconds` (default
  60/600/3600) before dead-lettering: always on for Azure Service Bus (native scheduling), opt-in
  via `MessageBus:EnableDelayedRedelivery` on RabbitMQ (requires the delayed-message-exchange
  plugin, which the Aspire dev container does not ship). `RegisterIntegrationEventConsumer` also
  registers a `FaultIntegrationEventConsumer<TEvent>` (opt out per event with its
  `registerFaultConsumer` parameter) that turns an otherwise silent `_error`-queue row into one
  structured Error log plus a `broker.fault.count` metric on the new `MMCA.Common.Broker` meter.
- **Circuit breaker on the outbox broker publish (ADR-009 revision).** The outbox processor's
  `IMessageBus.PublishAsync` call now runs under a Polly circuit breaker
  (`BrokerResilienceDefaults`: 0.5 failure ratio, 10 minimum throughput, 30s sampling, 15s break),
  so a dead broker sheds load fast instead of stacking publish timeouts; open-circuit failures
  follow the normal re-lease path and increment `broker.circuit.open.count`. A per-query database
  breaker was evaluated and rejected this wave; EF's retrying execution strategy remains the DB
  posture.
- **Authorization and Timeout decorators in the CQRS pipeline (ADR-014 revision).** Commands and
  queries implementing `IRequiresPermission` are checked against
  `IPermissionRegistry.HasPermission` with `ICurrentUserService.Roles`; a denial short-circuits
  with a Forbidden error and increments `cqrs.authorization.denied.count`, and the decorator sits
  outside caching so a denied query never reads or populates the cache. `IHasTimeout` requests run
  under a linked token cancelled after their budget; expiry returns a `Request.TimedOut` failure
  and increments `cqrs.timeout.count` while caller cancellation still propagates. New order:
  FeatureGate, Authorization, Logging, Caching, Validating, Timeout, Transactional.
- **Rate limiting settings, sliding window, and a distributed option (ADR-019 revision).**
  `RateLimitingSettings` (section `RateLimiting`) now backs `AddCommonRateLimiting` with an
  `Algorithm` choice (fixed or sliding window), and `Distributed=true` with a registered
  `IConnectionMultiplexer` moves the global and `UserPolicy` partitions onto a Redis-backed
  fixed-window limiter (INCR plus EXPIRE, fail-open on Redis unavailability); `auth-ip` stays
  in-process by design. Existing signatures delegate unchanged.
- **Feature-flag targeting (ADR-031 revision).** `CurrentUserTargetingContextAccessor` is
  registered via `WithTargeting`, so the built-in Targeting and Percentage filters now work
  through the existing `IFeatureGated` pipeline with no decorator change: percentage and
  group-targeted rollouts become a pure configuration exercise.
- **Specification-driven repository reads with projection pushdown (ADR-055 revision).**
  `QuerySpecification` adds ordering, string includes (with the collection split-query
  auto-switch), paging, and tracking control on top of the criteria-only base;
  `And`/`Or`/`Not` now compose by parameter substitution with per-instance caching and gain
  fluent extension forms. `IEntityQuerier` gains `ListAsync` (entity and projected overloads),
  `CountAsync` and `AnyAsync` by specification, and `IEntityQueryService` accepts
  `ISpecification` everywhere. An optional `IEntityDTOProjector` lets the generic read path
  project to DTOs in SQL instead of materializing full entities (PushNotification ships the
  reference projector with a value-equivalence test against the instance mapper).
- **Keyset pagination and deterministic paged ordering.** `GetPageByCursorAsync` pages by an
  opaque, versioned base64url cursor of (sort value, id) with `Result`-based validation of
  malformed cursors and unknown sort columns. Offset paging is now deterministic: an unsorted
  paged read orders by Id and every caller sort gains an Id tie-break.
- **Three new build gates (ADR-015 revision).** A namespace dependency-cycle fitness rule
  (signature-level, SCC-based, with a justified-allowance hook; one pre-existing Infrastructure
  cycle is exempted with per-edge rationale), a trailing-`CancellationToken` declaration rule for
  public async methods in Application and Infrastructure, and
  `Microsoft.CodeAnalysis.PublicApiAnalyzers` on every in-slnx Source project with committed
  `PublicAPI.Shipped.txt` baselines, so an accidental public-API break now fails the local build
  instead of first surfacing in the consumer canary.

### Changed

- **Inbox stays opt-in but is loudly off (ADR-021 revision).** A broker-connected host running
  without `MessageBus:EnableInbox=true` now logs a startup warning naming the duplicate-side-effect
  risk. Enabling the inbox needs no schema work on a migrated relational host (the
  `InboxMessages` table is part of the shared model; Cosmos hosts skip it).
- **Dependency refresh.** xunit.v3 4.0.0 and Testcontainers 4.14.0 (testing group), plus the
  August minor/patch group (EF Core and ASP.NET 10.0.11, dotnet/extensions 10.9.0, Meziantou
  3.0.163, Playwright 1.62.0 and more). Consumers were pre-aligned on 2026-08-18 so the version
  sweep resolves a coherent graph.

### Known consumer-sweep notes

- Consumer `DecoratorPipelineOrderTests` subclasses need `ICurrentUserService` and
  `IPermissionRegistry` test doubles once they take this version (the new decorators are in the
  default expected lists).
- A literal `CountAsync(null)` call is now ambiguous between the predicate and specification
  overloads and needs a cast.

## [1.152.0] - 2026-08-14

The 2026-08-14 bug-hunt remediation wave (this entry was added retroactively on 2026-08-18; the
tag predates it). Twenty-three defects from the full 35-unit sweep fixed in one pass (Common
#245), spanning paging, filtering, and handler edge cases; `GetProjectedAsync` gained an
`ignoreQueryFilters` parameter, a defaulted-parameter-before-CancellationToken change that
required a positional-caller and Moq sweep in consumers. Two reported findings were refuted with
evidence rather than changed. Details: `Reports/BugHunt.md` ledger (workspace) and the consumer
bump PRs ADC #122 / Store #88.

## [1.151.0] - 2026-08-14

The ADR-078 recorded follow-up: owner-scoped CSV exports and formatter hardening.

### Added

- **Row scoping for the generic CSV export.** `EntityControllerBase.GetExportSpecification()` is a
  new `protected virtual` hook whose result is applied to every page `GET {controller}/export`
  streams, through the specification parameter `IEntityQueryService.GetAllAsync` already carries for
  authorization filtering (no Application-layer change). It returns null by default, so a controller
  that overrides nothing queries unscoped and produces the byte-identical file 1.150.0 produced. A
  controller whose list endpoints row-scope reads should override it with the same specification,
  and can then relax the privileged-role gate it put on `/export` as the 1.150.0 interim mitigation:
  with the specification in force it is the query, not the role, that keeps one caller out of
  another caller's rows, so an owner can export their own data again.

### Fixed

- **CSV export omits columns it cannot render faithfully.** Binary properties (`byte[]` and
  `ReadOnlyMemory<byte>`, which covers the ubiquitous `rowVersion` concurrency token) and
  collection-typed properties used to fill every cell with a type name (`System.Byte[]`, the
  internal materialized-list type) even though the export deliberately queries with
  `includeChildren: false`. They now produce no column at all, in both the reflection column path
  and the shaped `fields=` path. Value objects and other class-typed properties are untouched and
  still render through their invariant `ToString`. A `fields=` request that names a dropped property
  now fails with the same `Error.InvalidEntityField` validation failure the JSON endpoints return
  for an unknown field, before a byte of body is written, rather than silently returning a file
  missing a column the caller asked for.

## [1.150.0] - 2026-08-14

The enterprise capability wave: eight opt-in features in one release. Everything below is inert
until a host calls the matching registration extension, so upgrading the pins alone changes no
behavior.

### Added

- **Multi-tenancy (shared-schema + database-per-tenant).** `ITenantEntity` entities gain a named
  `"Tenant"` EF query filter composing with the existing `"SoftDelete"` filter (one cached model
  serves every tenant; the tenant value is a query parameter). `TenantResolutionMiddleware`
  resolves claim-then-header per `Tenancy` settings (fail-closed `RequireTenant` default),
  `TenantSaveChangesInterceptor` stamps `TenantId` on insert and throws `CrossTenantWriteException`
  on cross-tenant writes, and per-tenant `DataSources` overrides route a tenant onto its own
  database through the same `DataSourceKey` (outbox drained per `(source, tenant)` pair; startup
  migrations get a per-tenant pass). A null tenant is the system context and sees all rows.
  Opt in with `AddMultiTenancy(configuration)`.
- **Recurring job scheduler.** `IScheduledJob` (cron via Cronos, UTC) over a `ScheduledJobs` store
  in the Default relational source, claim-leased by `ScheduledJobRunner` so exactly one replica
  runs a due job; missed occurrences run once then advance. Config cron overrides win over code
  defaults; OTel meter `MMCA.Common.Scheduler`. Opt in with `AddScheduledJobs(configuration)` +
  `AddScheduledJob<TJob>()`.
- **Audit trail.** `IAuditedEntity` aggregates get field-level change history
  (`AuditTrailEntries`, one row per changed property, written in the same transaction as the data;
  `[Pii]` values are stored as `[REDACTED]` on both sides). Retention ships as the first framework
  scheduled job (`audit-trail-cleanup`). Minimal `IAuditTrailReader` read surface. Opt in with
  `AddAuditTrail(configuration)`.
- **Data-subject export (DSAR).** `ExportUserDataHandlerBase<TUser, TQuery>` hoists the
  ADC/Store export idiom (ownership gate, best-effort `IUserDataExportSection` fan-out that
  degrades a failing section to `Available = false`), with a `UserDataExportDTO` wire envelope in
  Shared and an abstract `DataExportControllerBase` (authorized, gated by the new
  `Privacy.DataExport` feature flag) for hosts that want the JSON download surface.
- **CSV export on the generic entity controllers.** `GET {controller}/export` streams RFC 4180
  CSV (UTF-8 BOM, ISO 8601 invariant dates, columns matching the JSON field names) through the
  existing query pipeline, honoring `fields`, `filters` and sorting, capped by
  `ApplicationSettings.MaxExportRows` (default 100000) with an `X-Export-Row-Limit` header and a
  trailing truncation marker. A distinct route by design: the public output-cache policy does not
  vary by `Accept`.
- **`Enumeration<TEnumeration>` smart-enum base** in `Shared/ValueObjects` (closed sets with
  behavior: `FromValue`/`FromName` returning `Result<T>`, frozen lookups, name-based JSON via
  `EnumerationJsonConverterFactory` registered in `AddAPI`, EF `EnumerationValueConverter` pair).
- **Azure Key Vault configuration provider.** `AddCommonKeyVaultConfiguration()` (Aspire package)
  wires `KeyVault:Uri` into the configuration pipeline via `DefaultAzureCredential`; no-op when
  the key is absent, optional reload interval, requires the Key Vault Secrets User role the
  deployment sample already grants.
- **HybridCache substrate (opt-in).** `AddCommonHybridCache()` swaps `ICacheService` to a
  HybridCache-backed implementation (L1 + L2, stampede protection) under a disjoint
  `hc:` keyspace so the old and new serialization formats can never cross-read; counters bypass
  the local cache to keep L2-only semantics. `ICacheService` gains a non-breaking
  `GetOrCreateAsync` default member. The default path without the call is unchanged.

### Changed

- **`IPhysicalDbContextFactory` gained an abstract member** `Create(DataSourceKey, PhysicalDataSource)`
  (per-tenant routing). Custom implementors must add it; the framework implementation and all
  shipped call sites are updated.
- **Repository `ignoreQueryFilters: true` now means "include soft-deleted rows" only**: the
  tenant filter survives via the named-filter overload, so an admin read can no longer silently
  cross tenants. Callers of EF's own parameterless `IgnoreQueryFilters()` on raw `Table` surfaces
  still bypass every filter; prefer the repository parameter.
- New pins: `Cronos` 0.13.0, `Microsoft.Extensions.Caching.Hybrid` 10.8.0 (10.9.0 skews shared
  Microsoft.Extensions transitives to 10.0.11), `Azure.Extensions.AspNetCore.Configuration.Secrets`
  1.5.1.

## [1.149.0] - 2026-08-13

A single-fix release: the scoped bulk mark-read introduced in 1.148.0 never persisted its marks.

### Fixed

- **Scoped mark-all-read silently no-oped.** `MarkAllNotificationsReadHandler`'s scoped branch
  joined the tracked `UserNotification` query with `PushNotification`'s no-tracking source, and an
  `AsNoTracking` call anywhere in a composed EF query switches the whole query to no-tracking, so
  the loaded rows were never tracked and `SaveChangesAsync` persisted nothing (the endpoint still
  returned 204). The scoped branch now joins the tracked `Table`; the `select un` projection
  materializes only `UserNotification`, so no `PushNotification` instances are tracked. Caught by
  MMCA.ADC's integration tier; new SQLite-backed regression tests in Infrastructure.Tests run the
  real handler against real change tracking and fail under the old code. Unscoped reads and the
  read-only scoped queries (inbox, unread count) were unaffected.

## [1.148.0] - 2026-08-13

Event-scoped notifications and palette-aware shared section styles.

### Added

- **Notification scope keys.** `PushNotification` gained an optional opaque `ScopeKey` (max 128,
  e.g. `event:2`) so a consuming app can scope the notification inbox, the unread badge and bulk
  mark-read to its current context. `SendPushNotificationRequest` and `PushNotificationDTO` carry
  the key as init properties (no constructor break); the inbox, unread-count and read-all endpoints
  accept an optional `scope` query parameter. Semantics: an absent scope keeps every query
  byte-identical to the legacy shape (the unread-count and mark-all-read paths only join
  `PushNotification` when a scope is supplied, so they never inherit its soft-delete filter); a
  supplied scope selects rows whose key is null or matches. Scope is a view filter, not a security
  boundary.
- **`INotificationScopeProvider` in `MMCA.Common.UI`**: resolves the app's current scope key.
  `AddNotificationUI()` registers a null-returning default via `TryAddScoped`, so apps that never
  register a provider (Store, Helpdesk) are byte-for-byte unaffected. `PushNotificationService`
  stamps the current scope onto unscoped sends and `NotificationInboxService` threads it through
  every scoped read, so what gets sent and what gets shown always resolve to one scope.

### Fixed

- **Shared section styles now follow the theme.** `.mmca-section-heading .mud-icon` and
  `.mmca-section-description` in `app.css` used hardcoded black values that vanished (watermark) or
  failed contrast (description text) on the dark palette; both now derive from the MudBlazor
  palette variables, with the `-light` heading variant keeping its white ink on dark bands.

## [1.147.0] - 2026-08-13

A single-fix release: the MAUI barcode-scan surface now follows the in-app language.

### Added

- **`UseCommonBarcodeScanner(Func<string> cancelText, Func<string> cameraDescription)` overload in
  `MMCA.Common.UI.Maui`**: the localization-correct way to wire the camera scanner. Both delegates
  are invoked once per scan, when the modal page is built, so the scan surface follows the user's
  in-app language choice. `MauiBarcodeScannerService` gained the matching
  `(Func<string>, Func<string>)` constructor.

### Fixed

- **The MAUI scan page no longer ignores the in-app language.** `UseCommonBarcodeScanner` resolved
  its cancel label and camera description at `MauiAppBuilder` time, which runs before
  `MauiCultureInitializer` restores the persisted culture (ADR-027), so the modal rendered in the
  DEVICE language for the life of the process even after the user switched language in the app.
  Both strings now resolve per scan through the new delegate overload. The existing string overload
  keeps working unchanged (it wraps the values as `() => value`), so no consumer has to move; its
  XML doc now states plainly that those values are fixed at startup and points heads with a language
  switcher at the delegate overload. This removes the limitation recorded in ADR-071.

## [1.146.0] - 2026-08-12

A single-fix release so the OpenAPI guard ships in the same consumer sweep as 1.145.0 (which was
tagged hours earlier and is superseded by this version in every consumer pin).

### Fixed

- **URL-segment-versioned routes no longer fail OpenAPI document generation.** A route such as
  `[Route("api/v{version:apiVersion}/orders")]` turned `GET /openapi/{documentName}.json` into a
  500: MVC leaves `ApiParameterDescription.ParameterDescriptor` null for a route token with no
  matching action parameter, and `Asp.Versioning.OpenApi` 10.2.1 dereferences it without a null
  check inside `XmlCommentsTransformer`. `AddCommonApiVersioning()` and `AddCommonOpenApi()` now
  register `ApiParameterDescriptorBackfillProvider`, an `IApiDescriptionProvider` that runs last and
  fills a placeholder descriptor wherever one is missing. This closes the 1.144.0 known issue below.
  The guard is deliberately general: an unbound `{tenant}` or `{region}` token fails identically,
  because it is the token being unbound, not the versioning, that produces the null. Existing
  descriptors are never replaced, so the guard goes inert once the upstream null check lands and can
  then be removed without a behavior change. No consumer action is required, and header-versioned
  hosts (the entire current consumer surface) are unaffected either way.

## [1.145.0] - 2026-08-12

A feature release adding a shared QR-code display component and a device barcode-scanning
capability to the UI packages, plus a security pin lifting a vulnerable transitive dependency.

### Added

- **`QrCodeImage` component in `MMCA.Common.UI`**: renders a QR code for a given `Payload` as an
  inline base64 data URI (QRCoder `PngByteQRCode`, managed path only). Required `AltText` parameter
  keeps consumers accessible by default; `PixelsPerModule`, `ErrorCorrection`
  (new `QrErrorCorrectionLevel` enum, default Medium) and `Class` cover sizing and styling. Blank
  payloads render nothing; the bitmap is re-encoded only when a bitmap-affecting parameter changes.
- **`IBarcodeScannerService` device capability in `MMCA.Common.UI`** (`bool IsSupported`,
  `Task<string?> ScanAsync(CancellationToken)`; never throws, `null` means cancelled, denied or
  unsupported), with a `NullBarcodeScannerService` fallback registered by
  `AddDeviceCapabilityDefaults()` beside the existing media-picker capability.
- **MAUI implementation in `MMCA.Common.UI.Maui`**: `UseCommonBarcodeScanner(cancelText,
  cameraDescription)` opts a head in (wraps ZXing's `UseBarcodeReader()` and overrides the null
  registration); `MauiBarcodeScannerService` pushes a modal scan page (2D formats) whose decode,
  cancel, back-gesture and token-cancellation paths all resolve exactly once. Supported on Android
  and iOS; other targets keep the null fallback.

### Dependencies

- New: `QRCoder` 1.8.0 (MIT) in `MMCA.Common.UI`; `ZXing.Net.Maui.Controls` 0.10.3 (MIT) in
  `MMCA.Common.UI.Maui`.
- **Security**: direct `SSH.NET` 2026.0.0 pin in `MMCA.Common.Testing` (and the Redis test tier)
  to lift the vulnerable transitive brought in by Testcontainers
  ([GHSA-q939-rpr3-3284](https://github.com/advisories/GHSA-q939-rpr3-3284), high severity,
  ScpClient recursive-download path traversal). CPM does not pin transitives, hence the direct
  reference; no MMCA code uses SSH.NET.

## [1.144.0] - 2026-08-11

A dependency-refresh release driven by the August Dependabot sweep, plus the framework changes the
Asp.Versioning 10.2 analyzers required. No consumer code changes are needed for header-versioned v1
APIs (the entire current consumer surface): the served OpenAPI path stays `/openapi/v1.json`.

### Changed

- **`AddCommonOpenApi()` now registers OpenAPI through the API-versioning builder**
  (`AddApiVersioning().AddOpenApi()`), and **`MapCommonOpenApi()` serves one document per discovered
  API version** via `MapOpenApi().WithDocumentPerVersion()`. For today's v1-only hosts the output is
  unchanged (`/openapi/v1.json`, name derived from the `'v'VVV` group-name format); once a host
  introduces v2.0 it gets its own `/openapi/v2.json` instead of bleeding into the v1 document. This
  adopts the guidance behind the new AV0029/AV0030 analyzers.
- **`AddCommonApiVersioning()` no longer restates inherited defaults**: the explicit 1.0
  `DefaultApiVersion` (already the framework default, AV0011) and the API-explorer copies of
  `DefaultApiVersion`/`AssumeDefaultVersionWhenUnspecified` (inherited from the versioning options,
  AV0024) are removed. Runtime behavior is identical.
- **New `MMCA.Common.API` dependency: `Asp.Versioning.OpenApi` 10.2.1**, which supplies the
  versioning-aware OpenAPI registration and the per-version document convention.

### Dependencies

- Asp.Versioning.Mvc / Asp.Versioning.Mvc.ApiExplorer 10.0.1 to 10.2.1 (ships new AV-prefixed
  Roslyn analyzers; consumers that reference these packages directly and pin them centrally must
  bump to 10.2.1 to avoid an NU1605 downgrade, and may see new AV diagnostics in their own hosts).
- MudBlazor 9.7.0 to 9.8.0 (consumers referencing MudBlazor directly must pin at least 9.8.0).
- AngleSharp 1.7.0 to 1.7.1, Scalar.AspNetCore 2.16.17 to 2.16.18.
- Analyzers: Meziantou.Analyzer 3.0.141, Roslynator.Analyzers 4.16.0, SonarAnalyzer.CSharp 10.32.0.

### Known issues

- Upstream Asp.Versioning 10.2.1 throws a `NullReferenceException` in its XML-comments OpenAPI
  transformer when a route uses URL-segment versioning (`v{version:apiVersion}`), returning 500 from
  the document endpoint. No MMCA consumer uses URL-segment versioning (all versioning is
  header-based), so nothing is affected today; tracked as a consumer-facing caveat until fixed
  upstream.

## [1.143.0] - 2026-08-07

A governance and documentation release: no behavioral changes, and the shipped assemblies are
functionally identical to 1.142.0 (only an XML documentation line differs). Consumers take it as a
routine lockstep sweep (ADR-016).

### Changed

- **Namespace-matches-folder is now build-enforced repo-wide (IDE0130 at warning).** The shared
  analyzer baseline promotes `dotnet_style_namespace_match_folder` from suggestion to warning, so
  under `TreatWarningsAsErrors` a stale namespace after a file move fails the build. The deliberate
  flat-namespace surfaces (`Testing.Architecture` bases, `Testing.UI` infrastructure,
  `MMCA.Common.Aspire` DataProtection extensions, the API.Tests convention fake) are documented
  exceptions; their public namespaces are unchanged.
- **`OutboxSettings` retry-backoff XML documentation now mentions the retry jitter**, so the
  IntelliSense contract matches the processor's actual delay behavior.

## [1.142.0] - 2026-08-05

The Low-severity closure release: the bug-hunt ledger's entire remaining Common Low tail (L2-L5,
L8-L11, L14-L17), each adversarially re-verified against source before its fix. Nothing here is
breaking and no consumer migration is needed, but three items change observable behavior (see
Changed).

### Changed

- **`GetByIdAsync(id)` now respects the soft-delete filter (L4).** The id-only overload used
  `FindAsync`, which returns tracked soft-deleted entities and bypasses the global query filter. It
  now issues the same filtered query as the includes overload while staying tracked (the generic
  delete/update handlers depend on tracking); a soft-deleted id returns null.
- **A corrupt `Result<T>` payload now throws instead of deserializing as a fake success (L17).**
  JSON carrying neither `value` nor `errors` (truncated write, partial cache corruption, `{}`)
  produced `Result.Success(default)`. The generic converter now throws `JsonException`; the
  non-generic `Result` still round-trips `{}` as success by design.
- **Money display is currency-aware (L5, L14).** `ToDisplayString` rendered `$` for every currency;
  it now maps the currency to its symbol. `ToDisplayRange` computed one min/max across
  mixed-currency collections under the first item's code; it now renders one range per currency.

### Fixed

- **In-flight reads can no longer re-cache data a write just invalidated (L2).** A read racing the
  post-commit eviction could store the stale value it had already fetched. Post-commit invalidation
  now fires a second, delayed eviction to close the window; the cache service being a singleton
  makes the detached follow-up safe.
- **A transient JWKS key-build failure is no longer cached for the process lifetime (L3).** The
  provider's `Lazy<JsonWebKeySet>` used the default mode, which permanently caches a factory
  exception; `PublicationOnly` lets a later call retry.
- **`EfInboxStore` detaches the losing entry after a duplicate-key insert (L8).** The abandoned
  `Added` entity previously stayed tracked and failed every later `SaveChangesAsync` on that scope.
- **Auth-claim parsing is culture-invariant (L9).** `CurrentUserService` read numeric claims with
  the current culture while `TokenService` writes them invariantly; on some host cultures a valid
  claim silently parsed to null.
- **Int filters parse invariantly like their sibling strategies (L10).** `IntFilterStrategy` was
  the one strategy tracking the request culture; a failed parse fell through to an unfiltered query.
- **`NotificationHubService.StartAsync` is single-flight (L11).** Concurrent first starts could
  each build and start a hub connection, orphaning one (leaked socket, duplicate registrations).
  Disposal stays non-blocking and idempotent.
- **Warm-up port resolution survives a trailing slash (L15).** The `ASPNETCORE_URLS` fallback now
  selects the cleartext entry from a semicolon list and trims a trailing `/` before parsing the
  port, so self-HTTP warm-up no longer silently falls back to the default port.
- **The MAUI media picker no longer leaks a file stream on a pre-cancelled pick (L16).** The
  cancellation check now runs before the file is opened.

## [1.141.0] - 2026-08-04

A bug-hunt closure release: ten verified defects across Shared, Application, Infrastructure, API, UI
and MAUI, each reproduced against source before the fix. No public API changed and nothing here is
breaking, but one item changes a generated index and therefore needs a consumer migration (see
below).

### Fixed

- **Sub-1 page numbers no longer 500 the notification reads (H19).** Both notification handlers
  passed the raw `PageNumber` into `PaginationMetadata`, which throws on a negative, even though
  `PagingMath` had already floored the page to 1 for the query itself. The metadata page is now
  floored the same way, matching `EntityQueryService`.
- **A failed notification-hub connect no longer disables the hub for the process lifetime (H20).**
  `NotificationHubService` left the connection field populated after a terminal connect failure, so
  every later `StartAsync` no-opped at the null guard. Automatic reconnect only covers drops after a
  successful start, so live notifications never recovered. A connection that never started is now
  cleared and disposed.
- **Deleting an aggregate invalidates its cached reads (M50).** `DeleteEntityCommand` implemented no
  cache contract, leaving every cached read of a deleted aggregate stale. It now implements
  `ICacheInvalidating` against the aggregate-prefix convention consumers already key under; an empty
  prefix opts out.
- **`BETWEEN` rejects malformed ranges (M51).** The operator validated through `ParseList`, which
  drops unparseable and empty segments, so `5,abc,10` and `5,,10` passed as two-bound ranges and the
  strategy applied bounds the caller never asked for. Exactly two parseable segments are now
  required.
- **Cookie session refresh no longer serializes every user behind one lock (M52).**
  `CookieSessionRefresher` held a single process-wide semaphore across the outbound refresh call, so
  unrelated users' cold navigations queued behind whichever refresh was in flight. The lock is now
  striped by refresh token via `KeyedSemaphoreStripe`.
- **Concurrent token hydration stops losing a token (M53).** Both token storage services used an
  unguarded `??=` for single-flight hydration; on a genuinely multi-threaded Blazor Server circuit
  the later of two hydrates overwrote the other's token. The slot is now taken under a `Lock` and
  each caller clears only its own task.
- **A notification arriving after teardown no longer throws back at the hub (M54).** The
  `NotificationListener` callback dispatched a render unguarded, so an event delivered after the
  component was disposed surfaced inside the hub service's receive handler. It now carries the same
  `ObjectDisposedException` / `InvalidOperationException` guard as its sibling components.
- **MAUI token writes are ordered so a partial failure stays recoverable (M55).**
  `MauiTokenStorageService` wrote the access token before the refresh token with no rollback, so a
  failed refresh write left a new access token paired with a stale refresh token whose refresh path
  was already dead. Refresh is written first and a failed access write drops both. H14 semantics are
  unchanged.
- **`PaginationMetadata` cannot be constructed invalid (M56).** Validation lived only in the
  constructor, so object initializers, `with` expressions and System.Text.Json could each build a
  negative instance. The properties are now semi-auto and validate in `init`. The `init` accessors
  are deliberate: the record has two explicit constructors and no `[JsonConstructor]`, so
  deserialization runs through the parameterless constructor plus init setters, and removing them
  would produce all-zero metadata in every client reading a paged response.
- **Soft-deleted push notifications release their dedup slot (M58).**
  `PushNotificationConfiguration` declared its unique dedup index with a hand-authored filter, and
  `SoftDeleteUniqueIndexConvention` deliberately leaves hand-authored filters alone, so this was the
  one unique index on a soft-deletable entity without the `IsDeleted = 0` predicate. A soft-deleted
  notification therefore occupied its dedup key forever and every later send under that key was
  rejected by an index the application could not see. The index now opts in through
  `HasSoftDeleteFilter(additionalFilter: "[DedupKey] IS NOT NULL")`, the shared predicate builder the
  convention itself uses, which reads the column name from the model and the identifier quoting from
  the engine instead of hard-coding SQL Server syntax.

### Consumer action required

M58 changes a generated index, so consumers that map `PushNotification` need an EF migration in the
same sweep that takes this version. It is index-only (drop and recreate with the composed filter);
there is no table or column change and no data movement.

## [1.140.0] - 2026-08-04

A dependency-refresh release (Dependabot group #199, 16 minor/patch updates). No API or behavior
change. Notables: MassTransit 8.5.5 to 8.5.10 (stays on the v8 line per the license pin),
Grpc 2.80 to 2.83, bunit 2.8.6 to 2.9.0 and AngleSharp 1.6 to 1.7 (test tier),
Testcontainers.MsSql/RabbitMq 4.8 to 4.13, Microsoft.Data.SqlClient 6.1.1 to 6.1.6,
Asp.Versioning 10.0.1, System.IdentityModel.Tokens.Jwt 8.22.0, Scalar 2.16.17, and both analyzer
packages (Meziantou 3.0.138, Sonar 10.31) with zero new findings at error severity. Consumers must
align their own pins for the packages Common exposes without PrivateAssets (bunit, Grpc pair,
Asp.Versioning, IdentityModel, Scalar) in the same sweep to avoid NU1605.

## [1.139.0] - 2026-08-04

A one-item contract-honesty release.

### Removed (breaking)

- **`IEntityQueryService.GetAllForLookupAsync` loses its `orderBy` parameter.** The parameter was
  accepted and silently dropped since introduction: the repository contract has no ordering hook and
  the EF implementation always orders lookups by the projected display name, so every caller got
  name ordering regardless. No consumer passes the argument; the observable behavior of every lookup
  endpoint is unchanged. The compile-time break is confined to test mocks that enumerated the old
  five-argument signature (both apps' fixes ride this sweep). The interface remarks now document the
  fixed name ordering; if entity-level lookup ordering ever becomes a real requirement, the path is
  widening the repository contract, not restoring a decorative parameter. Repository-level
  `GetAllAsync` ordering is unaffected.

## [1.138.0] - 2026-08-03

The Wave 5 move-to-Common extraction (workspace extraction plan, wave 5): everything the
2026-08-03 cross-repo scan confirmed as duplicated between MMCA.ADC and MMCA.Store moves into the
framework, plus the long-deferred Identity use-case bases (plan items 3.2/3.3/3.4). Every addition
is additive: no existing public member changed, no EF schema change (the `OwnsMoney` and converter
swaps are proven facet-identical to the hand-rolled consumer blocks), and consumers keep their
error codes, routes, and behaviors byte-for-byte except where a change is called out below.

### Added

- **Kestrel endpoint extension** (`ConfigureEndpointsWithHealthProbe`, MMCA.Common.Aspire): the
  h2c-with-HTTP/1.1-health-probe listener profile previously copy-pasted across five service
  hosts, parameterized by `HttpProtocols` so mixed-protocol hosts are the same call.
- **`SelfHttpWarmupTaskBase`** (MMCA.Common.Aspire): the self-HTTP warm-up skeleton (server-started
  wait, port resolution, non-fatal error handling) shared by six service hosts; subclasses supply
  only `Name`, `WarmupPaths`, and optional HTTP-version/success-semantics overrides.
- **`WithE2eRegistrationThrottleLift`** (MMCA.Common.Aspire.Hosting): the E2E registration-throttle
  lift both AppHosts carried inline.
- **EF persistence helpers** (MMCA.Common.Infrastructure): `EmailValueConverter` /
  `PhoneNumberValueConverter` (plus nullable variants), the `OwnsMoney(...)` owned-type extension
  (including the `NoCurrency` materialization sentinel), and `HasSoftDeleteFilter` for hand-authored
  non-unique indexes (the sibling of `SoftDeleteUniqueIndexConvention`).
- **`IdentityModuleDbSeederBase<TUser>`** + `SeedAccount` (MMCA.Common.Infrastructure): the
  per-account seed idiom with app-supplied account lists, user factory, and an opt-in seed gate.
- **`CommonInvariants`** gains `EnsurePreferredCultureIsValid`, `EnsurePreferredThemeIsValid`,
  `EnsureMoneyIsNotNegative`, `EnsureIntIsPositive`, and `EnsureCollectionIsNotEmpty<T>`.
- **`OwnedByUserSpecification<TEntity, TId>`** (MMCA.Common.Domain): "owned by user" filtering over
  the audit `CreatedBy` column.
- **Identity contracts** (MMCA.Common.Domain): `IPasswordChangeableUser`, `IUserPreferences`, and
  `IErasableUser` (interface-dispatched delete, so aggregates that shadow `Delete()` stay correct).
- **Users use-case handler bases** (MMCA.Common.Application, plan item 3.2): ChangePassword,
  ChangePreferences, GetPreferences (read-repository path), and DeleteUser (virtual
  before/after-delete hooks plus a post-commit work queue), with `UserOwnershipRule` and the
  generic `SoftDeletedUserValidator<TUser>` (3.3), and shared `ChangePreferencesRequest` /
  `UserPreferencesResponse` / `GetUserPreferencesQuery` payload types (MMCA.Common.Shared).
- **`UserAccountAuthControllerBase`** (MMCA.Common.API): the password/preferences endpoints as a
  subclass of the unchanged `AuthControllerBase`; app commands stay app-side via factory hooks.
- **Testing**: `JwtTokenGenerator.ConfigureInProcessTokenValidation` (in-process JWT validation for
  cross-service tiers; adds the JwtBearer package dependency), `CrossServiceFixtureBase` +
  `TestPolling` (the multi-host Testcontainers SQL/RabbitMQ scaffold; adds Testcontainers.MsSql and
  Testcontainers.RabbitMq, test tier only), `DependencyInjectionAssert`,
  `ModuleConformanceTestsBase<TModule>` (Testing.Architecture), and `WebVitalsBudget` with
  `AssertWithinBudget` (Testing.E2E).
- **UI**: `AddUIModule<TModule>` module-registration extension. **UI.Maui** becomes the hybrid host
  layer: `MainPageBase` (back-navigation + JSRuntime dispatch glue), the hardened
  `MauiTokenStorageService` (degrades gracefully on OS keystore invalidation) with
  `AddCommonMauiTokenStorage()`, and the `NativeThemeSync` component.

### Fixed

- **`EntityQueryService.GetAllForLookupAsync` now forwards its `where` predicate** to the
  repository; previously the parameter was accepted and silently dropped, forcing consumers to
  hand-roll lookup readers.

### Changed

- **MMCA.Common.UI.Maui packaging**: the project moves to the Razor SDK to compile its new
  component; the WebView.Maui reference excludes build assets so consuming MAUI Blazor heads keep
  sole ownership of the static-web-asset pipeline. No consumer-visible API change.
- **Log text standardization**: the Users handler bases and warm-up base emit standardized message
  templates (for example "User {UserId} password changed"); levels and semantics are unchanged, but
  generator-assigned EventIds move to the shared holders, so telemetry keyed on EventId (rather
  than message text) needs re-pinning.

## [1.137.0] - 2026-08-03

A production-hardening release closing the high and medium impact gaps from the 2026-08-03
production-patterns audit: durable DataProtection for multi-replica hosts, request-body
fingerprinting and fail-open degradation for idempotency, cache-outage resilience, a governed SQL
command timeout, client-side idempotency keys, retry jitter, and outbox/cache/idempotency metrics.
Everything is additive or config-gated except the one signature change called out under Changed.

### Added

- **`AddCommonDataProtection()`** (MMCA.Common.Aspire): shared DataProtection key ring with Azure
  Blob persistence and optional Key Vault at-rest key encryption. Config-gated on
  `DataProtection:BlobStorageUri` so local dev keeps framework defaults; multi-replica hosts doing
  cookie or antiforgery crypto stop minting per-replica ephemeral key rings.
- **Idempotency request fingerprint**: `IdempotencyFilter` stores a SHA-256 hash of the request body
  and answers **422 Unprocessable Entity** when an `Idempotency-Key` is reused with a different
  payload (409 keeps meaning "original still in flight"). Records cached by earlier versions carry
  no hash and still replay unchanged.
- **Idempotency observability**: structured logging plus the new `MMCA.Common.Idempotency` meter
  (`idempotency.replayed`, `idempotency.conflict` tagged by kind, `idempotency.degraded`).
- **Outbox metrics** on the existing `MMCA.Common.Outbox` meter: `outbox.processed.count`,
  `outbox.dispatch.lag` (OccurredOn to ProcessedOn, seconds), and an `outbox.pending.depth` gauge.
- **Query-cache metrics**: `cqrs.query.cache.hit` / `cqrs.query.cache.miss` on the CQRS meter.
- **`Persistence:CommandTimeoutSeconds`** (default 30): a governed, overridable SQL command timeout
  applied by `SQLServerDbContext`.
- **Client-side idempotency keys**: new shared `IdempotencyHeaders` constant;
  `EntityServiceBase.AddAsync` now emits a stable `Idempotency-Key` reused across every retry
  attempt, and `AuthenticatedServiceBase.NewIdempotencyKey()` serves hand-rolled services.
- **`SoftDeletedUserCache`** helper: single home for the `user:deleted:{id}` marker key shape, so
  identity modules can write the marker through on delete instead of relying on an eviction whose
  prefix never matched.
- **Push send dedup**: optional `DedupKey` on `SendPushNotificationCommand` and `PushNotification`
  backed by a filtered unique index; a replay returns the original notification instead of sending
  twice. `NotificationsController.SendAsync` is now `[Idempotent]` and maps the client key into the
  command.

### Changed

- **Cache reads fail open.** `CachingQueryDecorator`, `IdempotencyFilter`, and
  `SoftDeletedUserMiddleware` now treat cache faults as misses (logged and counted) instead of
  failing the request: a Redis outage degrades to uncached reads and unguarded writes rather than
  500s on every authenticated request. The soft-delete check falls back to the validator query and
  only then proceeds open; 15-minute access tokens bound the exposure.
- **UI retry policy**: exponential backoff gains jitter, 408 and 429 are retried, 501 and 505 are
  not, and the policy honors the caller's `CancellationToken`; the outbox retry backoff gains
  jitter in the same pass.
- **Shared `APIClient` timeout**: the UI-layer HTTP client is bounded at
  `HttpResilienceDefaults.TotalRequestTimeout` (90s) instead of the 100s framework default.
- **Breaking (direct callers only)**: `SoftDeletedUserMiddleware.InvokeAsync` gained an `ILogger`
  parameter. Convention-activated middleware usage (every known consumer) is unaffected.

### Security

- **`System.Security.Cryptography.Xml` lifted to 10.0.10.** The DataProtection package chain pulls
  10.0.7 (five high advisories); projects with the AspNetCore framework reference prune it, but
  Aspire AppHosts resolve it as a real package. MMCA.Common.Aspire now references the patched
  version directly (pruning disabled for the project so the pin reaches the nuspec and package-mode
  consumers inherit it).

## [1.136.0] - 2026-08-03

A shared-layout fix release: the desktop sidebar now stays pinned while the page scrolls,
and the signed-in user name no longer renders twice on a phone. Both are pure UI changes in
MMCA.Common.UI; consumers pick them up with the pin bump and no code change.

### Fixed

- **The desktop sidebar stays pinned while the page scrolls.** It is exactly one viewport tall and
  relies on `position: sticky`, so on any page taller than the screen its background stopped one
  viewport down and the page background showed beside the content. Sticky resolves against the
  nearest ancestor scroll container, and two rules were creating dead ones that never scroll:
  `html, body { overflow-y: auto }` in `app.css`, and `.page { overflow-x: hidden }` in
  `MainLayout.razor.css` (a non-visible `overflow-x` forces the computed `overflow-y` from
  `visible` to `auto`). The first is removed; the second is now `clip`, which bounds horizontal
  overflow without establishing a scrollport. Consumers get the fix with no code change.

### Changed

- **The signed-in user name no longer repeats on the mobile top row.** `NavMenu`'s
  `.toprow-user-name` span is removed. That row only ever renders below 1024px, which is exactly
  where the hamburger menu's `.nav-auth-section` already shows the name above Logout, so it was
  always a duplicate and it competed for room on the narrowest row in the layout. On a phone the
  name is now visible after opening the menu rather than always on screen; hosts wanting an
  always-visible indicator should add an avatar or account icon via their app-bar components.

## [1.135.0] - 2026-08-01

The BugHunt remediation release: 24 verified defects fixed across persistence, event delivery,
the API boundary, shared contracts, and the query/caching/UI pipeline (workspace BugHunt ledger
M1-M14, M29-M39). Every finding was adversarially re-verified against source before its fix was
implemented. Four changes are behavioral and called out explicitly below.

### Changed (behavioral)

- **`SafeDomainEventHandler` now rethrows instead of swallowing.** The class always promised that a
  failed handler would be retried via the outbox, but the swallow meant the dispatch pipeline marked
  the rows processed and the work was lost permanently. Failures now propagate (log-and-rethrow
  filter; `OperationCanceledException` passes through unlogged), so the outbox retry and dead-letter
  paths engage. Consequence on the interceptor path: one rethrowing handler skips the processed
  stamp for that save's entire local batch, so every local event in it redelivers. Delivery is
  at-least-once; subclasses must tolerate repeats, including repeats of sibling events. No shipped
  consumer subclasses this type today.
- **Malformed money payloads fail fast.** The Shared `CurrencyJsonConverter` rejects non-string
  JSON tokens with a `JsonException` (parity with the API-layer converter), and `Money`'s
  `[JsonConstructor]` throws `ArgumentNullException` on a null currency instead of materializing an
  instance that fails later in `Add`/`IsZero`. "No currency" is expressed by the `Currency.None`
  sentinel, never null; EF value converters in consumers must materialize the sentinel for
  empty or unknown stored codes (MMCA.Store already does as of its 2026-08-01 deploy).
- **Direct repository saves stamp the acting user.** `EFRepository.Save()`/`SaveChangesAsync()`
  route through the `ApplicationDbContext` user-id overloads, so audit columns record the real
  actor instead of the system sentinel. No production caller exists today; the change protects
  future direct-save callers.
- **Sort validation accepts mapped DTO names, and pagination metadata is corrected.** Sort-column
  validation now consults `DTOToEntityPropertyMap` first (parity with filter validation), so mapped
  names that previously returned 400 validate and sort. Pagination metadata is built through the
  validating constructor with floors applied: `pageSize=0` no longer reports `PageSize=0` for a
  one-row fetch, the offset-overflow sentinel reports the clamped page size, and an unpaginated
  read of more than the 1000-row materialization cap now reports the 1000 actually returned rather
  than the total.

### Added

- **`IDistributedLock`** (Application) with a Redis implementation (`SET NX PX`, owner-token Lua
  release) and an exact-key in-process fallback, registered like the cache with an optional
  `IConnectionMultiplexer`. The idempotency filter uses it to close the cross-replica
  double-execution window; when the lock is held elsewhere it waits, replays the holder's stored
  response, and only conflicts when nothing was stored. Hosts without Redis keep prior behavior.
- **`TransactionCommitAmbiguousException`**: a transient failure during the commit phase no longer
  re-enters the execution-strategy delegate (which could duplicate a commit that actually landed);
  it surfaces as this wrapper with the original as inner. Recovery is the API's `[Idempotent]`
  replay. The multi-source sequential-commit case is documented as a known limitation.
- **User-scoped push-device deletion**: `IPushDeviceRegistrar.DeleteAsync(userId, installationId, ...)`
  (default interface implementation keeps external implementors compiling). The devices endpoint
  verifies installation ownership via the `user:{userId}` tag and returns 204 for unknown and
  not-owned ids alike, so deletion is no longer possible with a leaked installation id and the
  response shape leaks no existence information.
- **`TryParse<TIdentifier>` companion** to the deliberately coercing `Parse`, for callers that must
  distinguish malformed input from legitimate defaults (bool/enum route values especially).

### Fixed

- **Persistence**: the bounded multi-pass save loop throws (naming the dirty contexts) instead of
  silently dropping changes materialized after the third pass; domain-event capture skips exactly
  the entries the identity-insert path temporarily hides as Unchanged, so their events dispatch
  with the round that actually inserts the rows (events raised on genuinely Unchanged aggregates
  keep dispatching).
- **Event delivery**: a graceful shutdown mid-batch best-effort persists the in-memory processed
  stamps (bounded 5s token, never masks the cancellation), closing the 300s lease-expiry
  redelivery window; `BrokerEventBus` batch publish stages all rows in one save with one signal.
- **Background services**: every warmup task is bounded (120s), so a hanging dependency lands on
  the log-and-continue path instead of keeping `/health/ready` closed and the replica out of
  rotation indefinitely.
- **API boundary**: the idempotency filter stores and replays body-less 2xx responses (204) without
  stamping a content type; a registration losing the unique-index race now surfaces
  `Auth.EmailAlreadyExists` instead of a generic conflict; a `ResultFailureException` with no
  errors surfaces its message as the gRPC detail while preserving `StatusCode.Internal` in all
  four call shapes.
- **Caching**: prefix eviction scans every non-replica Redis server with per-server error
  tolerance and deletes per key (a cross-slot multi-key `DEL` throws on the production OSS-cluster
  topology); `MemoryCacheService` serializes set/remove/prefix-remove per key and scopes eviction
  callbacks to their own entry token, closing a race that left live entries invisible to prefix
  invalidation; the client-keyed projection and accessor caches are capped at 512 entries each,
  with past-cap requests computed per request (projection is skipped rather than compiled per
  request).
- **CQRS decorators**: the FeatureGate/Validating decorators build their failure factories lazily,
  so a handler whose result type is not `Result`/`Result<T>` resolves instead of dying with
  `TypeInitializationException` at first resolve.
- **UI**: `MobileInfiniteScrollList.ResetAsync` cancels the in-flight fetch and a generation
  counter discards stale appends and duplicate page fetches; `ThemeToggle` and
  `MmcaThemeProviders` guard their theme-change rerenders against disposal.

## [1.133.0] - 2026-07-30

### Fixed

- **Changing the language on a MAUI Blazor Hybrid head now takes effect immediately.** 1.132.0 fixed
  the switcher navigating to a server endpoint no hybrid head hosts, but the language still did not
  change: it only appeared after the next cold start, and only if the process was genuinely killed
  rather than swiped away.

  `CultureInfo.CurrentCulture` and `CurrentUICulture` are backed by an `AsyncLocal`, so the value flows
  with the `ExecutionContext` and is restored every time that context is re-entered, taking precedence
  over `DefaultThreadCurrentUICulture`. `MauiCultureInitializer` runs inside `MauiAppBuilder.Build()`,
  before any window exists, so the context it wrote to is the ancestor of every later dispatch,
  including the Blazor renderer's. A switch could then set the thread defaults and force-load the
  WebView, and the re-attach would re-enter the startup context, restore the launch culture, and render
  the old language for the rest of the session.

  `MauiCultureStore.ApplyToProcess` now sets the thread defaults and nothing else. With no code path
  assigning `Current*`, no thread or context materializes a culture of its own, so the defaults govern
  everywhere and a switch lands across the whole app at once. Web heads were never affected: request
  localization sets the culture per request, inside each request's own context, so nothing long-lived
  is pinned. Verified on an Android emulator, switching live with no process restart.

- **The mobile top-row actions no longer overlap the hamburger.** `.toprow-actions` carried
  `min-width: 0` plus the default `flex-shrink: 1`, so the `width: 100%` brand container squeezed it
  below its own content width. Its contents are `nowrap` with `overflow: visible`, so they spilled into
  the `4.5rem` that `.top-row` reserves for the absolutely positioned `.navbar-toggler`.

  Signed out, the spill was small enough to clip only the theme toggle. Signed in there are two more
  items, so it grew large enough to hide the user name behind the hamburger almost entirely.
  `flex: 0 0 auto` pins the group to its content width and lets the brand give way instead, which is
  what `.top-row .container-fluid` (`min-width: 0`) and `.navbar-brand` (`text-overflow: ellipsis`)
  were already set up to do. This was never MAUI-specific: every phone-width web render had it.

- **Preference writes no longer spend a 401 per theme or culture toggle on a rejected session.** The
  write is best-effort, so the response was never inspected and a 401 does not throw. The only guard
  was that the token be non-empty, which an expired token passes, and the shared auth handler neither
  refreshes nor retries. A session the API had stopped accepting therefore cost one silent failed
  request per toggle, with no user-visible symptom, which at low traffic is enough on its own to trip a
  failed-request alert rule.

  `ApiUserPreferenceWriter` now skips the call when the token is not usable (reusing the freshness
  check token storage already applies before re-acquiring), and skips it when the current token is one
  the API has already rejected. It remembers the rejected token rather than latching a flag, so a fresh
  sign-in resumes writing with no reset step. `ApiUserPreferenceReader` gets the freshness guard for
  the same reason. Nothing changes for the user: the choice is applied and persisted locally before the
  write, and the caller could never observe the failure.

## [1.132.0] - 2026-07-29

### Fixed

- **Switching language now works on MAUI Blazor Hybrid heads.** The culture switcher navigated to
  `/culture/set`, a server endpoint mapped by `MapCultureEndpoint()`. A hybrid head hosts no ASP.NET
  pipeline, so the Blazor `Router` resolved that path instead, matched no page, and rendered the
  not-found page: on Android, picking Spanish answered "Page Not Found" and dropped the user off the
  page they were on. The same call sits on the login path, so signing in with a stored `es` preference
  on a device running in English landed on the not-found page instead of the destination.

  Culture application is now behind `ICultureApplier`. `AddUIShared` keeps the web behaviour as the
  default (`EndpointCultureApplier`, the same endpoint round trip, unchanged for web heads);
  `MMCA.Common.UI.Maui` supplies `MauiCultureApplier`, which switches the process culture, persists the
  choice to device preferences, and reloads the WebView so the tree re-renders under the new culture.
  `MauiCultureInitializer` restores the persisted culture during `MauiAppBuilder.Build()`, so the
  choice survives an app restart and the first render is already correct. Both are wired by
  `UseMauiDeviceCapabilities()`, so hybrid heads need no code change; `UseMauiCulture()` is separately
  callable for a head that composes its own registrations.

  `<html lang>` now follows the active culture on every head. A Blazor Web head emits it server-side
  from `CurrentCulture`, but a hybrid head serves a static `index.html` that cannot be templated, so it
  kept declaring the hardcoded language after a switch and misreported the page language to assistive
  technology (WCAG 3.1.1). The new non-visual `DocumentLanguage` component, rendered once by
  `MainLayout`, sets it on first interactive render; it is a no-op on web heads, where the server
  already emitted the same value. Note that no automated gate catches this class of defect: axe checks
  that `lang` is present and well-formed, never that it is correct.

  `SupportedCultures.ResolveClosest` is new: it resolves an arbitrary culture name to the closest
  allowlisted one (exact match, then language, then the default), so a hybrid head starting from a
  device locale of `es-MX` gets `es` rather than falling back to English. Web heads already got this
  from request localization's `Accept-Language` matching; this keeps the two paths from drifting.

## [1.131.0] - 2026-07-28

### Fixed

- **Optional infrastructure health checks no longer gate readiness.** The Redis and RabbitMQ checks
  registered by `AddInfrastructureHealthChecks` are now tagged `optional` and excluded from
  `/health/ready`; they still report on `/health`.

  This made the method unsafe to adopt. `/health/ready` includes every check not tagged `live`, so a
  host calling `AddInfrastructureHealthChecks` had its readiness gated on Redis. Both consumer apps
  degrade gracefully without Redis (`DistributedCacheService` falls back to `MemoryCacheService`), so
  a cache blip would have taken EVERY replica out of rotation simultaneously and converted a partial
  degradation into a total outage. The database check stays untagged and still gates readiness, which
  is correct: a host that cannot reach its own database cannot serve correct responses.

  New public `HealthCheckTags` (`Live` / `Ready` / `Optional`) replaces the magic strings and
  documents the distinction. Tag a check `Optional` when the app has a working fallback; leave it
  untagged only when the app genuinely cannot serve without it.

## [1.130.0] - 2026-07-28

### Changed

- **`AuthControllerBase` now applies the anti-spray throttle by default.** `LoginAsync` and
  `RegisterAsync` carry `[EnableRateLimiting(RateLimitPolicyAuthIp)]`, so every consumer inheriting
  the base gets per-IP protection on its anonymous credential endpoints without opting in.

  This closes the half-measure in 1.129.0: that release lifted the *policy* into the framework but
  left each app to *attach* it, and an app that simply inherited these actions (MMCA.Store) silently
  had no spray protection at all. The global limiter deliberately no-ops for anonymous traffic and
  account lockout is per-email, so a spray (one password, many emails) from one source was
  unthrottled there.

  `RefreshAsync` is deliberately NOT throttled: refresh is automatic and periodic, and Blazor Server
  issues it server-side, so every Server-circuit user shares the UI host's IP. A per-IP window would
  throttle ordinary token renewal for everyone behind that host. A test asserts that absence so a
  later "consistency" change has to argue with it.

  **Consumer note:** a host inheriting `AuthControllerBase` must call `AddCommonRateLimiting()`, or
  it fails at startup on an unregistered policy. All current consumers already do. Consumers that
  already attach the attribute on their own overrides (MMCA.ADC) are unaffected; the explicit
  attribute is redundant but harmless.

## [1.129.0] - 2026-07-28

Extraction wave from the 2026-07-27 drift analysis: reusable infrastructure that had been duplicated
across MMCA.ADC and MMCA.Store moves into the framework. Consumers adopt during the version sweep.

### BREAKING

- **`MMCA.Common.API`: `AddCommonRateLimiting` now registers an `"auth-ip"` policy.** A consumer that
  already registers a policy of that name must DELETE its own registration when bumping, because
  `RateLimiterOptions.AddPolicy` throws on a duplicate name and the host will fail at startup.
  MMCA.ADC is the only consumer in that position today.

  ```csharp
  // Before (per-consumer, in the Identity service host):
  var authIpPermitLimit = builder.Configuration.GetValue("RateLimiting:AuthIp:PermitLimit", 30);
  services.AddRateLimiter(options => options.AddPolicy("auth-ip", httpContext => { /* ... */ }));

  // After: delete the block above. The policy ships with AddCommonRateLimiting; tune it there.
  services.AddCommonRateLimiting(authIpPermitLimit: 30);

  // Endpoints reference the shared constant instead of the string literal:
  [EnableRateLimiting(WebApplicationBuilderExtensions.RateLimitPolicyAuthIp)]
  ```

### Added

- **`MMCA.Common.API`: per-IP anonymous authentication throttle** (`RateLimitPolicyAuthIp`,
  `authIpPermitLimit` defaulting to 30 req/min). The global limiter deliberately no-ops for anonymous
  traffic and account lockout is per-email, so a password spray from one source was otherwise
  unthrottled. Fails OPEN on an unattributable client IP, so the in-process TestServer (where
  `RemoteIpAddress` is null) is never throttled.
- **`MMCA.Common.Aspire`: SQL Server readiness check** in `AddInfrastructureHealthChecks`, with an
  opt-in `requireSqlServer` fail-fast. The asymmetry with the silently-skipping Redis and RabbitMQ
  branches is deliberate: an optional cache may legitimately be absent, a service database may not.
  Adds `AspNetCore.HealthChecks.SqlServer` 9.0.0, matching the Redis/RabbitMQ sibling version.
- **`MMCA.Common.Testing.Architecture`: `ObservabilityConventionTestsBase`**, the SLO alert-to-runbook
  pairing gate. Reads the embedded `infra.main.bicep` / `infra.OPERATIONS.md` from the DERIVED type's
  assembly via a virtual `ResourceAssembly`, so a subclass needs no extra wiring.
- **`MMCA.Common.Testing`: `GracefulShutdownTestsBase<TEntryPoint>` and
  `ProductionHostApplicationFactory<TEntryPoint>`.** The factory pins the environment to `Production`
  (exercising the realistic CORS/HSTS branches a default Development boot skips) and captures the
  started `IHost` so the shutdown test can drive `StopAsync` directly.

## [1.128.0] - 2026-07-25

Distribution release. **No source changes: the compiled assemblies are identical to 1.127.0.** What
changes is where the packages are published, how the release authenticates, and what the package
listing looks like. **No consumer action is required**, and bumping to this version is optional for
anyone already on 1.127.0.

### Added

- **The packages are published to nuget.org.** Until now they went only to GitHub Packages, whose
  NuGet registry requires a personal access token with `read:packages` **even for public packages**
  (only its Container registry allows anonymous pulls). The consequence was that
  `dotnet add package MMCA.Common.API`, the line printed in the README, in the getting-started
  guide, and throughout the article series, failed for everyone outside the owning account: a 401,
  not a package. A nuget.org search for `MMCA.Common` returned no hits at all. Both registries now
  receive every release from the same tag and the same pack step, so they cannot drift in content.
  GitHub Packages is retained as a mirror, not deprecated. See ADR-053.

- **Package listing metadata**, which is the entire first impression for anyone arriving from search
  rather than from the docs: `PackageProjectUrl`, `PackageIcon` (a new 128px brand mark packed once
  for all packages), and `PackageTags`, all set once in `Directory.Build.props`. The repository
  README was already packed into every package and now serves as the listing page.

### Changed

- **Release authentication is keyless.** Both publishing jobs request a GitHub OIDC token
  (`permissions: id-token: write`) and exchange it through `NuGet/login@v1` for an API key valid for
  one hour, immediately before the push. No long-lived credential exists in the repository, so there
  is nothing to leak and nothing to rotate; a stored key would have carried a 365-day maximum
  lifetime and failed the release on expiry. The exchange is authorized by a policy on nuget.org
  pinned to the permanent GitHub ids of the owner, the repository, and **this workflow file**, which
  is what defeats a delete-and-recreate resurrection attack. The trade-off is that the workflow file
  name is now load-bearing: renaming or splitting `release.yml` breaks publishing until the policy
  is updated. nuget.org itself now marks API keys "Not recommended" for automated publishing.

- **The README is rewritten as the package listing page it actually is.** It ships inside every
  package, so it now leads with what the framework is and why it exists, the install line, the
  fifteen-minute path through MMCA.Helpdesk, and links to the reference library, the ADR index, the
  scorecard, and the article series. Relative links became absolute, because relative links do not
  resolve on nuget.org. Drops a stale ADR range and a path that has not existed since the ADRs moved
  to the Website repository.

- **The em dashes are gone from all fifteen package `Description` values.** That text is the listing
  copy on nuget.org, and em dashes are banned across this workspace.

## [1.127.0] - 2026-07-25

Performance release from a second evidence-led pass over the framework's read, save and background
paths. No breaking changes and no public API changes: every item is a cost reduction or a fix to
something that was measurably doing more work than it needed to. Two items change what reaches the
database (emitted SQL, and two new indexes), so read the migration note below before sweeping.

**Consumer action required:** the two new outbox/inbox indexes need one EF migration per consumer
service database. Nothing else in this release touches a consumer.

### Fixed

- **Dynamic-LINQ filter values were inlined into SQL as literals instead of parameters.**
  Every filter strategy and the dynamic sort called System.Linq.Dynamic.Core with the default
  `ParsingConfig`, which leaves `UseParameterizedNamesInDynamicQuery` off, so each `@0` argument
  became a `ConstantExpression` and EF inlined it. A filtered read emitted
  `WHERE [Name] = 'Widget'` rather than `WHERE [Name] = @p`. Every distinct filter value therefore
  produced distinct SQL text: a SQL Server plan-cache entry per value, no plan reuse across them,
  and a miss in EF's compiled-query cache on every request, on the hottest read path in every
  consumer. A single shared parameterizing config is now threaded through all 58 dynamic-LINQ call
  sites. Two requests filtering the same property with different values now produce byte-identical
  SQL. New `ToQueryString` assertions pin this; nothing in the suite inspected emitted SQL before,
  which is why it went unnoticed.

- **The outbox signal accumulated one wake-up per save instead of one per batch.**
  `OutboxSignal` used `new SemaphoreSlim(0)`, whose maxCount is `int.MaxValue`, so the
  `SemaphoreFullException` catch at the release site was unreachable and permits piled up. After a
  burst of N event-raising saves the processor woke N times, and each surplus cycle issued a
  candidate-fetch query per relational data source that returned nothing. Capped at one permit,
  which the existing catch already anticipated: one batch drains everything.

- **Both retention sweeps ran unindexed.**
  `IX_OutboxMessages_Pending` is filtered `WHERE [ProcessedOn] IS NULL`, which excludes exactly the
  rows `OutboxCleanupService` looks for, so the six-hourly processed sweep scanned the largest
  partition of the table; the inbox purge filtered `ProcessedOn` against a table indexed only on
  `MessageId`. Adds `IX_OutboxMessages_Processed` (filtered `IS NOT NULL`) and
  `IX_InboxMessages_ProcessedOn`. The pending index also gains `RetryCount` and `LockedUntil` as
  included columns, since the poll filters on both and every candidate row was costing a key lookup.

- **Change detection ran three times per save.**
  `ChangeTracker.Entries<T>()` triggers a full `DetectChanges` on every call and memoizes nothing.
  The audit interceptor and the domain-event interceptor each scan the tracker from `SavingChanges`,
  and EF detects again before building the save, so a save paid three O(tracked entities x
  properties) snapshot comparisons where one suffices. `ApplicationDbContext` now detects once up
  front and suppresses automatic detection for the rest of the save, restoring the caller's setting
  afterwards. Safe because the interceptors write through `entry.Property(...).CurrentValue` and
  `Add`, both of which take effect without detection.

- **The by-id fast path was unreachable for any entity declaring a navigation.**
  Introduced in v1.119.0, it required zero includes, but the REST by-id action defaults
  `includeFKs` to true. Those reads fell back to the dynamic-filter pipeline and emitted
  `SELECT TOP(1000) ... WHERE Id = @p` with a client-side `FirstOrDefault`, where a keyed `TOP 1`
  would do. Supported includes are now passed into the repository's include overload, which applies
  the same `Include` calls and auto-applies `AsSplitQuery` for child collections. Cross-source
  ("unsupported") includes still take the pipeline, since only its navigation populator can
  batch-load across physical sources.

### Changed

- **Per-request allocations on the query pipeline and the decorator pipeline.**
  `LoggingQueryDecorator` allocated a dictionary and boxed scope state on every query at every log
  level, including when logging was disabled; it now uses `LoggerMessage.DefineScope` like the
  command decorator already did, and both time with `Stopwatch.GetTimestamp` rather than allocating
  a `Stopwatch`. `QueryFieldService` rebuilt its `MemberInit` projection tree per request; the
  projection and the shaping-accessor subset are now cached per entity type and normalized field
  set. `RepositoryFactory` reflected over constructors on every repository resolution and now caches
  a compiled `ObjectFactory` per closed type. `EntityDataSourceRegistry.GetPhysicalSourcesInUse`
  re-projected over every registered entity on each call (both background services call it every
  poll) and is now precomputed on the snapshot.

- **`ExistsAsync` no longer applies the Cosmos `COUNT` workaround to every provider.**
  It branches on provider, so everything except Cosmos gets `AnyAsync` and short-circuits at the
  first match instead of reading every matching row.

### Added

- **Regression coverage for the paths this release touches.** `QueryParameterizationTests`
  (emitted-SQL assertions, including the plan-reuse invariant stated directly),
  `SaveChangeDetectionTests` (exactly one detection pass per save, plus that suppression costs no
  behaviour), `OutboxSignalTests`, and five `[MemoryDiagnoser]` benchmarks over `ApplyFilters`,
  `ApplySorting` and `ShapeCollectionData` with allocation ceilings committed to
  `perf-baseline.json`, so the per-request parse and shaping cost cannot grow unnoticed.

## [1.126.0] - 2026-07-25

Hardening release from a read of the framework's request and background paths. No breaking changes:
every item is a behavioural fix or a bound on something previously unbounded. The two settings
additions have defaults that preserve existing behaviour except where that behaviour was the bug.

### Fixed
- **The dynamic-filter property cache grew without bound from the query string.**
  `QueryFilterService` memoized failed lookups as well as successful ones in a `static`,
  never-evicted dictionary keyed by names taken straight from `?filters[X].operator=...`. Any caller
  could grow it for the life of the process, one nonexistent filter name at a time, while each
  request came back as a well-formed 400 that showed nothing in error metrics. Only resolved lookups
  are cached now; a miss costs a reflection lookup instead. `QueryFilterModelBinder` gained a
  per-request cap of 50 distinct filters to bound that cost (surplus entries are dropped, not
  rejected, since they were never valid filters).

- **Paginating the notification inbox and history overflowed to a negative `OFFSET`.**
  Both handlers derived `(PageNumber - 1) * PageSize` in 32-bit, which wraps negative for page
  numbers near `int.MaxValue`. SQL Server rejects a negative `OFFSET` outright rather than treating
  it as zero, so the request became a 500 instead of the empty page that page genuinely holds.
  `EntityQueryPipeline` had already solved this in 64-bit; the arithmetic now lives once in
  `PagingMath.Clamp` and all three call sites use it. It also floors the page size, which the
  `[Range]` attributes at the API boundary enforce but a direct handler caller does not inherit.

- **A nested transaction threw instead of joining the ambient one.**
  `DbContextFactory.BeginTransaction` began a transaction on every cached context unconditionally,
  while `CommitTransaction` and `RollbackTransaction` both skipped contexts without one. An
  `ITransactional` command whose handler also called `ExecuteInTransactionAsync` therefore hit
  `InvalidOperationException` from EF. `ExecuteInTransactionAsync` is now re-entrant: an inner call
  joins the ambient transaction and returns directly, and begin, commit, rollback and the
  deferred-event flush belong to the outermost call alone. Note that simply skipping the second
  begin would have been worse than the exception: the inner commit would then make the outer scope's
  earlier work durable ahead of its own decision, turning nesting into a silent partial commit.

- **A failed outbox message was retried on the lease, not on a schedule.**
  `OutboxProcessor` incremented `RetryCount` on failure but left the row's claim in place, and the
  poll skips leased rows, so a failure was retried only after the full `Outbox:LeaseSeconds` (300s by
  default) regardless of `PollingIntervalSeconds` or an explicit signal. The retry cadence was an
  accident of the lease rather than a decision. Failures now re-lease for an explicit exponential
  backoff, `Outbox:RetryBackoffBaseSeconds` (default 10) doubling per attempt, capped at the lease.

- **Nested filter paths ignored the type they pointed at.**
  Every dotted path (`Category.Name`) was routed to the string strategy regardless of its leaf type,
  so a nested non-string leaf passed validation for a string-only operator such as `IS EMPTY` and
  then failed inside Dynamic LINQ at query-build time: a 500 for what is really a bad request. The
  leaf type is now resolved by walking the path, identically in `ApplyFilters` and `ValidateFilters`.
  A path that cannot be walked keeps the previous string behaviour, so nothing that works today
  starts failing.

- **The `async void` capability listeners could take the host down.**
  `DeepLinkListener`, `PushRegistrationListener` and `OfflineBanner` caught only
  `ObjectDisposedException` and `InvalidOperationException`. Anything else escaped onto the thread
  pool with no caller to observe it: process termination under MAUI, an unobserved exception outside
  the circuit's error handling under Blazor Server. `PushRegistrationService.RegisterAsync` reaches
  the network and a platform token provider, so it was the likeliest to throw something else. All
  three now log exhaustively.

### Added
- `Outbox:RetryBackoffBaseSeconds` (default 10, capped at `LeaseSeconds`): the base of the
  exponential backoff applied to a failed outbox message before it is retried.
- A Docker-gated Redis integration tier (`Tests/Core/MMCA.Common.Infrastructure.Redis.Tests`, outside
  the `.slnx`, its own CI job) exercising `DistributedCacheService` against a real Redis. This is the
  tier that would have caught the 1.125.0 counter regression fixed in 1.125.2: the unit tests mock
  `IDistributedCache`, so they assert the calls made and never the storage format Redis ends up
  holding, and a counter written as a string and read back as a hash round-trips perfectly against a
  mock. It covers the counter round-trip, TTL application, concurrent increments, and prefix
  eviction over a live `SCAN`.

### Changed
- The `LoginProtectionService` comments no longer claim the attempt counters are incremented
  atomically. They have not been since 1.125.2 traded atomicity for readability, and they now name
  the residual weakness plainly: concurrent guesses can undercount, so a burst can stay under
  `MaxFailedAttempts`, while sequential guessing still trips the lockout. Closing that gap needs
  either a Lua script against the hash layout or moving counters off `IDistributedCache`.

## [1.125.2] - 2026-07-24

Patch fixing a regression introduced by `ICacheService.IncrementAsync` in 1.125.0.
**Consumers must take this instead of 1.125.0 or 1.125.1.** Those two versions break registration
and login on any host backed by Redis.

### Fixed
- **The Redis `INCR` fast path wrote a value its own reader could not parse.**
  `DistributedCacheService.IncrementAsync` used `StringIncrementAsync`, which writes a Redis
  **string**, while `StackExchangeRedisCache` stores every entry as a Redis **hash** (`absexp` /
  `sldexp` / `data`, read back with `HMGET`). The first increment created a string at the counter's
  key; the next read of that key failed with `WRONGTYPE`, which surfaced as a 500 on the endpoint
  owning the counter. In practice that is **registration** (`CheckRegistrationRateLimitAsync` reads
  the per-IP counter before every sign-up) and the ADR-029 **login lockout** counters. A host using
  the in-memory cache was unaffected, which is why the framework's own tests and CI stayed green;
  MMCA.ADC's end-to-end suite caught it, failing every registration after the first.

  `IncrementAsync` now goes through `IDistributedCache` for both the read and the write, so the
  counter is stored the same way it is read. It is therefore a read-modify-write again and can
  undercount under genuinely concurrent increments; for a brute-force counter a rare lost increment
  is a much smaller problem than the counter being unreadable. Restoring atomicity means either
  running the whole update as one Lua script against the hash layout, or moving counters out of
  `IDistributedCache` so both sides speak Redis strings.

## [1.125.1] - 2026-07-24

Patch fixing a regression introduced by the `ICurrentUserService.Roles` addition in 1.125.0.
**Consumers should take this instead of 1.125.0.**

### Fixed
- **`Roles` and `IsInRole` broke against implementations that do not expose a full principal.** The
  new `Roles` default interface member read role claims off `User` and nothing else. Two problems
  follow from it being a *default interface member*, which runs against every implementation rather
  than only the `CurrentUserService` shipped here:
  - It dereferenced `User` unguarded. The shipped implementation never returns null (it falls back to
    an empty `ClaimsPrincipal`), but a mocked `ICurrentUserService` returns null for an unconfigured
    reference property, so an `IsInRole` check became a `NullReferenceException`.
  - It ignored `Role`, which is also part of this interface. An implementation that populates `Role`
    but not a full principal reported *no* roles, silently turning an authorization check into a
    denial.

  `User` is now null-guarded, and `Roles` falls back to `Role` when the principal yields no role
  claims. Claims still win when present, so a multi-role principal is read in full. Caught by
  MMCA.ADC's suite on the 1.125.0 bump (38 failures, then 7 after the null guard alone); with this
  fix its 2227 tests pass with no consumer change.

## [1.125.0] - 2026-07-24

Correctness release from a review of the MMCA.Common and MMCA.ADC codebases, plus additive feature
work extracted from the MMCA.Store Sales consistency-guards PR (Store #39). No public API is removed,
but **three fixes change behavior at the API edge** and are called out under Changed below: the
ownership filter now denies by default, unparseable filter values are rejected instead of ignored,
and only successful responses are cached for idempotent replay.

### Security
- **Login lockout could be bypassed by changing an email's capitalization (ADR-029).**
  `LoginProtectionService` built its `login:lockout:` and `login:attempts:` keys from the raw request
  string while the user lookup resolved through the `Email` value object (trim + lowercase), so
  `User@x.com`, `user@x.com`, and a padded variant targeted one account but got three independent
  counters and lockouts: cycling capitalization reset the exponential backoff indefinitely. Keys now
  route through the same normalization, with a trim-and-lowercase fallback for malformed addresses
  (which never match a user but still increment a counter).
- **Idempotency keys were global, so one caller's response could be replayed to another (ADR-017).**
  The cache key was `idempotency:{client-supplied header}` and nothing else, so two callers choosing
  the same value shared an entry and one user's serialized response body was served to the other;
  with services sharing a cache instance the collision also crossed endpoints and services. The key
  is now `SHA-256(subject | method | route template | client key)`, where subject is the `user_id`
  claim or `anon:{remote address}`.
- **The ownership filter failed open (ADR-033).** `OwnerOrAdminFilter` called `next()` whenever the
  owner parameter could not be resolved (absent, non-int, or inside a bound model), so it silently
  stopped guarding any action whose parameter was optional or non-integer. It now denies by default;
  see Changed.

### Fixed
- **Domain events raised by an in-process handler were silently discarded (ADR-003).** The post-dispatch
  cleanup cleared each aggregate's event list wholesale, which also wiped anything a handler raised on
  that same aggregate during dispatch: those events arrived after the capture and were removed before
  any later capture could see them, so they never dispatched and never reached the outbox. Capture now
  snapshots each aggregate's events and removes exactly those, via the new
  `IAggregateRoot.RemoveDomainEvents`.
- **An execution-strategy retry duplicated inserts and outbox rows (ADR-003).**
  `ExecuteInTransactionAsync` re-runs its delegate against the same cached `DbContext` instances, so
  entities added by a failed attempt were still `Added` and were inserted again, and because capture
  runs on every `SavingChanges` pass while events are cleared only after a successful save, each
  attempt appended another outbox row per event: one transient SQL failure published every integration
  event twice. Retries now reset the change tracker, and an abandoned capture's staged rows are
  discarded.
- **A save could throw "Collection was modified".** `DbContextFactory` enumerated its context
  dictionary while each save dispatched domain events in-process; a handler reaching a
  not-yet-materialized data source calls `GetDbContext`, which writes into that dictionary mid-loop.
  Every enumeration now works from a snapshot, and `SaveChangesAsync` re-loops (bounded) so a context
  materialized during the save is still saved rather than skipped.
- **Prefix cache invalidation could miss a live entry (ADR-026).** `MemoryCacheService` removed a key
  from its tracking table on *every* eviction reason, but `IMemoryCache` queues post-eviction callbacks
  to the thread pool, so overwriting a live key could delete the tracking record for the entry that
  just replaced it. The entry stayed cached but invisible to `RemoveByPrefixAsync`, clearable only by
  its TTL. The callback now skips `EvictionReason.Replaced`.
- **A renamed filter property was validated and then silently dropped (ADR-034).** `ApplyFilters` fell
  back to the DTO property name while `ValidateFilters` fell back to the mapped entity name, so a plain
  `DTOToEntityPropertyMap` rename passed validation and was then skipped, returning an *unfiltered*
  result set with a 200. Both now share one resolver.
- **Pagination edges.** A page number near `int.MaxValue` overflowed the checked 32-bit Skip offset into
  a 500 instead of the empty page it describes (now 64-bit and range-checked); an unpaginated read
  reported the 1000-row safety cap as `TotalItemCount`, claiming the set was exactly that size (now
  counts only when the materialized rows reach the cap); and `PaginationMetadata.PageSize` reported the
  requested size rather than the clamped one the pipeline applied.
- **`IsInRole` saw only the first role claim.** It compared against `ICurrentUserService.Role`, so a
  principal holding several roles failed the check for all but whichever was listed first. Latent while
  tokens carry one role, and it would have surfaced as a silent authorization denial. Added
  `ICurrentUserService.Roles` (default interface member) and redefined `IsInRole` over it.
- **The outbox processor burned retries on shutdown (ADR-003).** Its general `catch` also caught the
  cancellation raised at host shutdown, incrementing `RetryCount` and stamping `LastError` on the whole
  remainder of the batch, so a graceful restart could dead-letter messages never actually attempted.
- **The keyed by-id fast path was unreachable (ADR-034).** `IsPrimaryKeyOnlyLookup` treated `includeFKs`
  as disqualifying while `EntityControllerBase.GetByIdAsync` defaults it to `true`, so every REST by-id
  read fell through to the dynamic-filter pipeline (parsed string predicate, `TOP 1000`, client-side
  `FirstOrDefault`) where a keyed `TOP 1 WHERE Id = @id` would do.
- **The query-cache lock table could grow without bound.** Its comment claimed it was bounded by the set
  of distinct cache keys, which holds only for parameterless keys; any `CacheKey` embedding a user id or
  filter value grew it indefinitely. Both it and the idempotency lock now use the new striped lock.

### Added
- **`KeyedSemaphoreStripe` (Shared).** Fixed-width per-key lock. Replaces the one-semaphore-per-key
  dictionary, which forced a choice between two defects: removing the entry when the last holder
  releases lets one caller wait on a semaphore no longer in the table while a second creates a fresh one
  (both then execute, defeating the lock), and never removing it grows the table without bound.
- **`[AllowMissingOwner]` (API).** Explicit opt-out from the ownership filter's deny-by-default, for
  actions guarded another way (a row-scoping specification, or their own policy). The attribute is an
  assertion, so each application site must name the guard that replaces the check.
- **`ICacheService.IncrementAsync`.** Default interface member (no implementer breaks) with a Redis
  `INCR` override, for counters whose read-modify-write could lose concurrent increments.
- **`IFilterStrategy.CanParseValue`.** Default interface member returning `true`, implemented by the six
  value-type strategies, so custom strategies are unaffected until they opt in.
- **`Cache:KeyPrefix` (`CacheKeyPrefixOptions`).** Optional namespace for services sharing one cache
  instance. Deliberately applied inside `DistributedCacheService` rather than through
  `RedisCacheOptions.InstanceName`, which sits below this abstraction where `RemoveByPrefixAsync`'s SCAN
  cannot see it and would silently evict nothing.
- **`IWriteRepository.ExecuteUpdateAsync` set-based conditional update (Application + Infrastructure).**
  Symmetric counterpart to `ExecuteDeleteAsync`: one atomic `UPDATE ... SET ... WHERE ...` through the
  repository abstraction, intended for contention-proof conditional updates (stock decrements, quota
  claims) where zero rows affected means the guard did not hold and the database arbitrates races. The
  SET clause is described through the new EF-free `IUpdatePropertySetter<TEntity>` builder (fixed
  values or expressions over the current row), translated to EF Core `SetPropertyCalls` by the new
  `UpdatePropertySetterBuilder`. Global query filters (soft delete) apply to the WHERE; domain events
  are bypassed (as with `ExecuteDeleteAsync`); `LastModifiedOn`/`LastModifiedBy` are stamped
  automatically (TimeProvider clock + `ICurrentUserService` when available) unless set explicitly.
- **`ConcurrencyTokenRequest` (Shared).** Reusable request body for lifecycle/state-transition
  endpoints whose only payload is the ADR-035 optimistic-concurrency token: bind as an optional body
  (`EmptyBodyBehavior.Allow`) so body-less callers skip the stale-view check. Replaces per-app copies
  (Store's `OrderTransitionRequest`, ADC's lifecycle equivalents) at the next consumer sweep.
- **`PeriodicBackgroundService` (Infrastructure).** Base class for fixed-interval background sweeps:
  enablement gate, TimeProvider-driven startup delay + interval waits (deterministic in tests via
  `FakeTimeProvider`), and a failing cycle that is logged without killing the loop. For reconciliation
  and cleanup work (e.g. Store's stuck-payment sweep); deliberately not used by the signal-driven
  outbox processor.

### Changed
Three edge-visible behavior changes. Each replaces a fail-open default; review consumer endpoints
against them.

- **The ownership filter denies by default (ADR-033).** An action guarded by
  `[ServiceFilter(typeof(OwnerOrAdminFilter))]` whose owner parameter cannot be resolved is now
  rejected instead of allowed through. Because the filter is usually applied at controller level, this
  covers every action on that controller, including ones inherited from the entity controller bases.
  Audit them and mark the ones guarded another way with `[AllowMissingOwner]`.
- **An unparseable filter value is a 400 (ADR-034).** `?filter=id:equals:abc` previously returned the
  whole (capped) result set because the strategy could not parse the value and silently applied no
  predicate. Validation now rejects it with `Filter.Value.Invalid`.
- **Only 2xx responses are cached for idempotent replay (ADR-017).** Any `ObjectResult` used to be
  cached, including ProblemDetails failures, so a client retrying after a transient 500 kept receiving
  that 500 for the full 24-hour window instead of the retry executing.

### Notes
`IAggregateRoot` gains `RemoveDomainEvents`. The only implementer across all four repos is
`AuditableAggregateRootEntity`, so every aggregate inherits it and no consumer change is required.

### Added
- **`IWriteRepository.ExecuteUpdateAsync` set-based conditional update (Application + Infrastructure).**
  Symmetric counterpart to `ExecuteDeleteAsync`: one atomic `UPDATE ... SET ... WHERE ...` through the
  repository abstraction, intended for contention-proof conditional updates (stock decrements, quota
  claims) where zero rows affected means the guard did not hold and the database arbitrates races. The
  SET clause is described through the new EF-free `IUpdatePropertySetter<TEntity>` builder (fixed
  values or expressions over the current row), translated to EF Core `SetPropertyCalls` by the new
  `UpdatePropertySetterBuilder`. Global query filters (soft delete) apply to the WHERE; domain events
  are bypassed (as with `ExecuteDeleteAsync`); `LastModifiedOn`/`LastModifiedBy` are stamped
  automatically (TimeProvider clock + `ICurrentUserService` when available) unless set explicitly.
- **`ConcurrencyTokenRequest` (Shared).** Reusable request body for lifecycle/state-transition
  endpoints whose only payload is the ADR-035 optimistic-concurrency token: bind as an optional body
  (`EmptyBodyBehavior.Allow`) so body-less callers skip the stale-view check. Replaces per-app copies
  (Store's `OrderTransitionRequest`, ADC's lifecycle equivalents) at the next consumer sweep.
- **`PeriodicBackgroundService` (Infrastructure).** Base class for fixed-interval background sweeps:
  enablement gate, TimeProvider-driven startup delay + interval waits (deterministic in tests via
  `FakeTimeProvider`), and a failing cycle that is logged without killing the loop. For reconciliation
  and cleanup work (e.g. Store's stuck-payment sweep); deliberately not used by the signal-driven
  outbox processor.

## [1.124.0] - 2026-07-23

Maintenance release strengthening the architecture-test rule library. No breaking changes and no
public API removed; all existing factories in Common and the consumers already comply.

### Added
- **`DomainFactoriesReturnResult` fitness function (`MMCA.Common.Testing.Architecture`).**
  Generalizes the previously aggregate-only `Result<T>`-factory check to every `Create` factory on
  domain entities and value objects across the Domain and Shared layers, so a future factory that
  returns a bare value object or entity (bypassing invariant checks) fails the build. Wired into
  both `AggregateConventionTestsBase` and `EntityConventionTestsBase`, so Store, ADC, and Helpdesk
  inherit the rule through their existing subclasses (fitness methods 91 -> 93).

## [1.123.0] - 2026-07-22

Maintenance release removing a redundant integration-event publish abstraction. **Breaking:** the
`IIntegrationEventPublisher` interface and its `IntegrationEventPublisher` adapter are removed;
callers inject `IEventBus` directly. No runtime behavior changes.

### Removed
- **`IIntegrationEventPublisher` (Application) and `IntegrationEventPublisher` (Infrastructure).** The
  adapter delegated every call straight to `IEventBus.PublishAsync`, whose single-event overload has
  an identical signature, so it carried no behavior the interface below it did not already provide.
  Callers that injected `IIntegrationEventPublisher` now inject `IEventBus` directly (same
  outbox-persist-then-dispatch in the monolith, same broker swap via `AddBrokerMessaging`). Removing
  the adapter is the first step of the longer consolidation onto `IMessageBus` as the single outbox
  transport (see the events/outbox onboarding chapter).

## [1.122.0] - 2026-07-22

Feature release completing the dynamic-filter operator matrix, hardening pagination, and making
distributed cache invalidation observable. No breaking changes and no public API removed; every
existing filter, query, and cache behavior is preserved.

### Added
- **Dynamic-filter operator matrix completed.** `IS EMPTY` / `IS NOT EMPTY` null checks now work on
  `bool?`, `int?`, `decimal?`, and `Guid?` columns (previously only `DateTime?` could be filtered
  for null); `IN` is now supported by the decimal and DateTime strategies (parity with the existing
  int/Guid/string `IN`); and an inclusive `BETWEEN` range (`"min,max"`) is supported by the int,
  decimal, DateTime, and long strategies.
- **`LongFilterStrategy`.** `long` / `long?` properties are now registered in `QueryFilterService`
  by default (equality, comparison, `IN`, `BETWEEN`, and null checks). A `long`-keyed entity
  previously failed filter validation with "No filter strategy registered".

### Changed
- **Pagination backstop in `EntityQueryPipeline`.** The pipeline now clamps a request's page size to
  the framework ceiling (`MaxUnboundedResultLimit`) in both paginated paths, as defense in depth.
  The API-boundary `ApplicationSettings.MaxPageSize` clamp is unchanged; this guard means a direct
  Application-layer caller (a gRPC handler or cross-module call) that bypasses that boundary can no
  longer request an unbounded page.

### Fixed
- **Silent no-op cache invalidation is now observable.** When `DistributedCacheService` has no
  `IConnectionMultiplexer` (e.g. a SQL-Server-backed `IDistributedCache`, or Redis registered
  without a client), prefix eviction was a silent no-op. It now logs a warning (once for the
  steady-state missing-multiplexer case, every time for the anomalous no-server case) so a
  TTL-only-invalidation deployment is visible rather than invisible.
- **`MemoryCacheService.GetAsync<T>` no longer throws on a type-mismatched key.** A key reused under
  a different `T` now returns a clean cache miss instead of an `InvalidCastException`.

## [1.121.0] - 2026-07-21

Maintenance release: the C# 14 extension-block migration, the shared analyzer baseline with the
ConfigureAwait gate (ADR-049), and the 2026-07-20 NuGet audit sweep (SQLite advisory resolved, so
consumers can drop their matching suppressions at this pin sweep). No breaking changes and no
public API changes (the extension-block migration preserves the lowered static surface).

### Security
- **SQLite advisory GHSA-2m69-gcr7-jv3q (CVE-2025-6965) resolved.** `SQLitePCLRaw.bundle_e_sqlite3`
  now ships the patched native SQLite (pinned at 2.1.12, then bumped to 3.0.4) and is referenced
  directly by `MMCA.Common.Infrastructure` (same pattern as the MessagePack pin) so the fix flows
  to consumers through the published package graph. The `NuGetAuditSuppress` entry was removed and
  the accepted-advisory list is now empty (ADR-038 updated);
  `dotnet list --vulnerable --include-transitive` reports zero rows. Consumers can drop their own
  suppressions for this advisory when they take this version.

### Changed
- **C# 14 extension-block migration complete.** The 15 remaining classic `this T` extension classes
  (~40 methods) moved to `extension(T)` blocks, finishing the adoption started in the DI
  registration files. Methods stay methods, so the lowered static surface and binary compatibility
  with consumers are unchanged; internal `RuleHelpers` parameterless helpers became extension
  properties.
- **Shared analyzer baseline + ConfigureAwait gate (ADR-049).** `.editorconfig` restructured into a
  SHARED ANALYZER BASELINE region plus repo-specific deltas (workspace drift guard:
  `Tools/Scripts/compare-analyzer-config.ps1`); IDE0005 and S125 promoted to warning, CA1031 to
  suggestion; scoped per-glob rules replace repeated inline suppressions. CA2007 is now enforced
  for `Source/**` (UI component packages excluded): packaged non-UI code awaits with
  `ConfigureAwait(false)` (324-site sweep).
- **Dependency refresh (2026-07-20 audit + dependabot).** EF Core 10.0.10, OpenTelemetry 1.17,
  Azure.Identity 1.21, BenchmarkDotNet 0.15.8, MudBlazor 9.7, Microsoft.OpenApi 2.11.0,
  SQLitePCLRaw 3.0.4, Scalar.AspNetCore 2.16.16, Meziantou.Analyzer 3.0.124, plus other approved
  servicing bumps; MassTransit stays pinned to v8 by policy. CI: actions/setup-dotnet 5 -> 6;
  dependabot no longer rebases open PRs and ignores Microsoft.OpenApi majors.
- **Documentation library centralized in the Website repo.** ADRs, the rubric, scorecards,
  backlogs, and the narrative guides are canonical under `Website/docs-src/` (published at
  `https://ivanball.github.io/docs/`); `FACTS.md`, `CHANGELOG.md`, `SECURITY.md`,
  `NavigationFlow.md`, `CONTRIBUTING.md`, and the deployment sample doc stay in this repo.

## [1.120.0] - 2026-07-19

Correctness release from the 2026-07-19 full review: the event/transaction core, outbox
scale-out safety, and the previously-untested guarantees. Behavior changes below are
deliberate fixes; consumers must add one EF migration (two new nullable outbox columns and
filtered unique indexes) when adopting.

### Changed (behavior)
- **Transactional commands roll back on business failure** (`ITransactional` + a returned
  failed `Result`): previously the transaction committed, leaving partial writes when a
  handler saved and then failed a later invariant (ADR-014 revision).
- **In-process domain event dispatch is deferred until after commit** and dropped on
  rollback, so handler side effects never act on state that can still roll back and
  execution-strategy retries cannot double-dispatch.
- **Integration events raised via `AddDomainEvent` now route through the outbox to
  `IMessageBus`** (broker-correct); previously they were dispatched in-process and marked
  processed, silently never reaching the wire in extracted deployments (ADR-003 revision).
- **Sync `SaveChanges` is now symmetric**: captured events are cleared (previously a later
  async save re-captured them into duplicate outbox rows) and the audit user id is stamped.
- **Unique indexes on soft-deletable entities exclude deleted rows** via a new
  model-finalizing convention, so a soft-deleted row no longer blocks re-creating the same
  record; hand-authored index filters win. Consumers get index-altering migrations.
- **`QueryFieldService.ApplySorting` treats the DTO map plus real entity properties as a
  strict allowlist**: client-supplied sort strings can no longer reach Dynamic LINQ as
  nested paths or expressions.
- **`Result.Failure` with an empty error collection now throws `ArgumentException`**
  instead of fabricating a success carrying a null value.

### Added
- **Outbox lease/claim** (`OutboxMessage.LockedUntil` and `LockToken`, `Outbox:LeaseSeconds`):
  concurrent processor replicas can never double-dispatch; `minReplicas: 1` is now a cost
  choice, not a correctness requirement (ADR-030 note). Retry exhaustion emits an Error log
  and the `outbox.dead_letter.count` metric with `reason=retries_exhausted`;
  `Outbox:DeadLetterRetentionDays` keeps failed payloads longer than processed rows.
  `OutboxProcessor` accepts an injectable `TimeProvider`.
- **`ModuleLoader`**: explicit-assemblies `DiscoverAndRegister` overload (the AppDomain scan
  misses not-yet-loaded assemblies) and `ValidateRemoteDependencies(IServiceProvider)`, a
  startup check that every `RemoteDependencies` declaration actually resolves.
- **`MMCA.Common.Testing`**: `HandlerTestBase<THandler>` (the UnitOfWork/repository mock
  scaffold consumers copy-pasted per handler test) and `DecoratorPipelineOrderTestsBase`
  (asserts the ADR-014 nesting from a real built container). The package now depends on
  `MMCA.Common.Application` and Moq.
- **`MMCA.Common.Testing.Architecture`**: layer-map completeness facts on
  `LayerDependencyTestsBase` (a repo whose map omits a layer no longer passes vacuously);
  governance interfaces matched by full name; opt-in `HandlerResultConventionTestsBase`
  (handlers' `TResult` must be a `Result`) and `RawQueryableConventionTestsBase`
  (ban `IRepository.Table*` in Application code, with an allowlist ratchet).
- **`DataGridListPageBase.LoadFailed`**: pages can render a real inline error state instead
  of the indistinguishable "no records" empty state after a failed fetch.

### Fixed
- `SET IDENTITY_INSERT` is wrapped in try/finally: a failed save can no longer leave the
  flag on the pooled connection or strand hidden entities in the Unchanged state.
- Audit `CurrentSaveUserId` resets after every save, so an internal follow-up save cannot
  stamp rows with the previous caller's identity.
- Cache-stampede per-key lock no longer eagerly removes semaphores (a race could let two
  concurrent executions through); cross-instance protection documented as best-effort.

### CI
- New `package-consumption` job packs every package to a local feed and builds a throwaway
  consumer against the nupkgs, catching pack breaks and package-mode-only failures before a
  release. `--minimum-expected-tests` raised 1 to 2000; the GHSA suppression grep is scoped
  to actual `NuGetAuditSuppress` lines; the webkit pseudo-locale sentinel has a bounded
  in-test retry. Four Testing packages had their blanket NoWarn lists pruned (dead-code
  detectors re-enabled). Outbox tests run on `FakeTimeProvider` (Infrastructure test tier
  ~11s to ~3s).

## [1.118.0] - 2026-07-17

FinOps cost-knob release plus a dependency and analyzer refresh. No breaking changes and no API
changes; the two new telemetry knobs are opt-in (unset preserves current behavior).

### Added (2026-07-13 FinOps §31: metric-family cost knobs)
- **`Telemetry:DisableHttpClientMetrics` / `Telemetry:DisableRuntimeMetrics`** (`MMCA.Common.Aspire`,
  `ConfigureOpenTelemetry`, rubric §31): two opt-in boolean knobs that drop the two highest-volume,
  lowest-value OpenTelemetry metric families from export (HttpClient connection/request metrics and the
  .NET runtime `dotnet.*` metrics). On a low-traffic multi-service deployment these are ~85% of the
  `AppMetrics` data points and carry no end-user-visible signal; dropping them cut total Log Analytics
  ingestion ~70% on the MMCA apps. Unset (default) keeps both, so there is no behavior change for a host
  that does not opt in, and anything but a boolean `true` keeps the family (a typo cannot silently blind
  it). Server-side RED metrics (`http.server.*` / `aspnetcore.*` / `kestrel.*`) and `AppDependencies`
  traces are untouched. See `COST.md`.

### Security
- **AngleSharp pinned to 1.5.0 (CVE-2026-54570 / GHSA-pgww-w46g-26qg).** bUnit floors the transitive
  AngleSharp at 1.4.0, which carries a Moderate mXSS advisory (MathML `annotation-xml` handling). The two
  bUnit-referencing projects (`MMCA.Common.Testing.UI`, `MMCA.Common.UI.Tests`) now take a direct
  reference to the patched 1.5.0. Test-tier only; no production runtime surface.

### Changed
- **Dependency and analyzer refresh.** SixLabors.ImageSharp 3.1.11 -> 3.1.12, Meziantou.Analyzer
  and SonarAnalyzer.CSharp, OpenTelemetry.Api and OpenTelemetry.Instrumentation.Runtime, plus the CI
  action pins (checkout, upload-artifact, download-artifact). No public API changes.
- **ADR and scorecard refresh.** update-adrs drift fixes (ADR-036, ADR-041, ADR-042, ADR-045) and the
  twentieth-wave ArchitectureScorecard re-score with backlog reconciliation.

## [1.117.0] - 2026-07-16

Scorecard-uplift wave release: four new CI enforcement gates, the child-entity optimistic-concurrency
overload (ADR-035 amendment), and the Secondary brand-token single-sourcing. No breaking changes;
one behavior change consumers should note (the notification routes now carry `[Authorize]`).

### Added
- **Child-entity `SetOriginalRowVersion` overload (ADR-035 amendment, rubric §8).** New
  `MMCA.Common.Domain.Interfaces.IRowVersioned` (implemented by `AuditableBaseEntity<TId>`) lets
  `IWriteRepository.SetOriginalRowVersion(IRowVersioned childEntity, byte[]? rowVersion)` stamp a
  tracked CHILD entity's original concurrency token (e.g. a `ProductVariant` under a `Product`),
  with the same null-or-empty no-op contract as the aggregate-typed overload. Update handlers that
  mutate children through the aggregate's repository call it per child after loading.
- **Secondary brand tokens (rubric §20).** `BrandColors` gains `Secondary`/`SecondaryDark`/
  `SecondaryLight` (values unchanged: the palette previously hard-coded the same hex), `app.css`
  gains `--mmca-secondary`/`--mmca-secondary-dark`, both `MMCATheme` palettes source from the
  constants, and `BrandColorTokenTests` guards the new tokens plus palette sourcing.
- **`NavigationContractTests` (rubric §25).** Arch-tier drift gate asserting `NavigationFlow.md`'s
  routes/auth table matches the `RouteAttribute`/`AuthorizeAttribute` reality of `MMCA.Common.UI`
  (set-equality both ways, auth-posture consistency, non-vacuity floor).
- **Latency-regression gate (rubric §12).** The `performance-smoke` CI job now measures
  (`--job Short`, JSON export) and a new dependency-free `build/perfgate` verifier fails it against
  the committed `Tests/Performance/perf-baseline.json` (deterministic allocation ceilings + a
  1000x compiled-expression-cache ratio floor).
- **`sample-deployment-validate` CI job (rubric §17).** Compiles both `samples/deployment` Bicep
  templates on every push/PR so the reference IaC cannot rot silently.

### Changed
- **The notification routes are now guarded (rubric §25).** `/notifications`,
  `/notifications/inbox`, and `/notifications/send` carry `@attribute [Authorize]`, matching the
  documented contract (previously the routes were open and only the APIs enforced auth); an
  anonymous visit now redirects to `/login`. The send page's role/claim gate remains
  consumer-declared (NavItem filter) plus server-side API authorization.
- **The `/counter` template-leftover page was removed** (routable but unreferenced and
  undocumented), along with its orphaned resx pairs.
- **CI gates promoted to required (rubric §22/§33):** the webkit `ui-e2e` leg (11 consecutive green
  main runs) and the `consumer-source-build` Helpdesk canary (9 consecutive green runs) lost their
  `continue-on-error` and joined branch protection.
- **The Store-specific `.cart-drawer` responsive width tiers left `app.css`** (consumer CSS does
  not belong in the framework stylesheet); MMCA.Store carries the identical rules in its own CSS
  from this version's pin sweep.

## [1.116.0] - 2026-07-15

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.115.0] - 2026-07-11

### Added (2026-07-11 remediation wave 1: §18/§19 fitness gates, dark-mode a11y gate, gallery vitals)
- **`UIArchitectureConventionTestsBase`** (`MMCA.Common.Testing.Architecture`, rubric §18): file-scan
  fitness base enforcing the container/presentational split mechanically: every `*.razor.cs` under
  `Source/` stays within a 400-line cap, and every `.razor` file keeps its inline `@code` block within
  120 lines (substantial logic belongs in the code-behind partial). Extension points: `MaxCodeBehindLines`,
  `MaxInlineCodeLines`, `MinimumCodeBehindFiles` (non-vacuity guard), `ExcludedPathFragments`.
  Subclassed in-repo as `UIArchitectureConventionTests`.
- **`StateManagementConventionTestsBase`** (`MMCA.Common.Testing.Architecture`, rubric §19): fitness
  base failing the build on any mutable static field or settable static property in a repo's
  `Layer.Ui` assemblies (the Blazor Server cross-circuit state-leak shape; compiler-generated members
  excluded, deliberate exceptions recorded via `AllowedStaticMembers`), plus a source scan forbidding
  singleton registration of stateful UI services (`*StateService`/`*StateContainer`). Subclassed
  in-repo as `StateManagementConventionTests` (one recorded exception: the `ErrorMessages._localizer`
  write-once wiring extension point).
- **Dark-mode axe gate** (`DarkModeE2ETests`, rubric §20/§21): the gallery Login + Components pages are
  re-scanned with the dark palette active (seeded via the `mmca_theme` cookie) inside the blocking
  chromium `ui-e2e` job, closing the tracked dark-palette contrast item.
- **Gallery Core Web Vitals budgets** (`WebVitalsE2ETests`, rubric §23): LCP/TTFB/CLS measured on the
  gallery Login + Components pages with the shipped `WebVitalsCollector` and asserted against budgets
  in the blocking chromium `ui-e2e` job, so shared-chrome front-end performance is measured and
  enforced, not assumed.

### Changed (2026-07-11 remediation wave 1)
- **Dark palette WCAG AA contrast fix** (`MMCATheme.PaletteDark`): `PrimaryContrastText` and
  `ErrorContrastText` are now dark (`rgba(0,0,0,0.87)`), the Material dark-theme treatment for the
  lightened `Primary` (#42A5F5) and `Error` (#EF5350) shades. The filled-primary button label
  (was ~2.65:1) and the filled error-alert message text (was ~3.5:1) now pass the 4.5:1 floor. Filled
  primary/error surfaces render dark-on-color instead of white-on-color in dark mode.
- **`MobileInfiniteScrollList` and `NotificationBell` split to code-behind partials** (§18 conformance;
  markup and behavior unchanged, render-snapshot and bUnit suites green). `NotificationBell`'s event
  handlers dropped `async void` for explicit fire-and-forget discards and its `Dispose` adopted the
  standard `Dispose(bool)` pattern.

## [1.114.0] - 2026-07-11

### Added (2026-07-11 move-to-Common extraction wave, E1-E12 of `Docs/Planning/DriftAnalysis-plan.md` 2026-07-11)
- **`RouteAuthorizationTestsBase`** (`MMCA.Common.Testing.Architecture`): reflection fitness base
  asserting every governed routable Blazor page carries the required role, with extension points
  `TargetAssembly` / `RequiredRole` / `IsGovernedPage(Type)` / `MinimumGovernedPages` and a
  non-vacuity guard; replaces five hand-rolled per-repo copies. Attribute detection is
  full-name-based, preserving the package's zero-ASP.NET-reference design.
- **Contract-test bases** (`MMCA.Common.Testing`): `ServiceInfoVersioningContractTestsBase<T>`
  (the whole /ServiceInfo v1/v2 + version-headers body), `OpenApiContractTestsBase<T>`
  (document served + well-formed 3.x, extension points `MinimumPathCount` / `CorePublicResources`), and
  `ProblemDetailsContractTestsBase<T>` (RFC 9457 shape probes + the shared
  `AssertProblemDetailsShapeAsync` helper); app-specific 409 facts stay app-side.
- **UI HTTP-service test harness** (`MMCA.Common.Testing.UI`): `CapturingHttpMessageHandler`
  (responder-delegate AND route-registration modes, 404 default), `CapturedRequest`,
  `UiHttpServiceHarness` + `FreshApiClientFactory` (fresh "APIClient" per call),
  `StubTokenStorageService` (swappable `AccessTokenProvider`), and static `HttpTestDoubles`
  helpers; replaces four hand-rolled per-repo doubles sets.
- **`ClickAndWaitForUrlAsync`** (`MMCA.Common.Testing.E2E` `PageExtensions`): URL-navigation twin
  of `ClickAndVerifyAsync` (click + URL wait + reclick belt), lifted from Store's
  row-navigation helper.
- **`ImageContentSniffer`** (`MMCA.Common.Application`): dependency-free magic-byte jpeg/png/webp
  allowlist beside `IImageProcessor` (ADR-045); app-side size limits and error codes stay
  app-side.
- **`MapAppAssociationEndpoints` + `AppAssociationOptions`** (`MMCA.Common.API`): config-driven
  `/.well-known/assetlinks.json` and `/apple-app-site-association` mappers (ADR-043) beside the
  JWKS/OIDC mappers; the per-app applinks component list is passed via options.
- **`WithE2eRsaKeys`** (`MMCA.Common.Aspire.Hosting`): opt-in AppHost extension forwarding the
  E2E ephemeral RS256 keypair env vars onto the Identity resource, replacing the identical
  inline blocks in both consumer AppHosts.
- **`IFormFactor` concretes**: `WebFormFactor` (`MMCA.Common.UI.Web`, + `AddCommonWebFormFactor()`),
  `WasmFormFactor` (`MMCA.Common.UI`, + `AddWasmFormFactor()`), `MauiFormFactor`
  (`MMCA.Common.UI.Maui`, + `AddMauiFormFactor()`); replaces six per-host copies.
- **`BiometricGate`** (`MMCA.Common.UI` `Components/Capabilities`): the app-lock overlay component
  with its en/es resx pair (de-branded strings), plus `DevicePreferenceKeys.AppLockEnabled`
  (`"applock.enabled"`; consumers migrating from an app-prefixed key need a one-time app-side
  preference migration).
- **`MmcaThemeProviders`** (`MMCA.Common.UI`): the four Mud providers + the ADR-028 Day/Dark
  lifecycle in one component; `MainLayout` now renders it instead of carrying the inline block.

## [1.113.0] - 2026-07-11

### Added (2026-07-11 managed file storage + avatars, [ADR-045](https://ivanball.github.io/docs/adr/045-managed-file-storage-and-avatars.html))
- **`IFileStorageService`** with unconfigured Null default and `AzureBlobFileStorageService`
  swapped in by `AddAzureBlobFileStorage(configuration)` when the `FileStorage` section is
  complete (`ContainerName` + `ServiceUri` for DefaultAzureCredential auth or
  `ConnectionString` for local Azurite). New pins: `Azure.Storage.Blobs`, `Azure.Identity`.
- **`IImageProcessor`** with `ImageSharpImageProcessor` (always registered): decodes untrusted
  uploads, bakes in EXIF orientation, center-crops to an exact square, strips ALL metadata
  (EXIF GPS is PII), and re-encodes as JPEG so only pixels survive; undecodable content is a
  validation failure. New pin: `SixLabors.ImageSharp` (Six Labors Split License; Apache 2.0
  terms apply to this project's use).
- **`IMediaPickerService` UI capability** (ADR-042 pattern) with `MauiMediaPickerService` in
  UI.Maui (photo pick/capture, permission flow encapsulated, cancelled/denied returns null);
  web heads keep the Null default and render an `InputFile` instead.

### Added (2026-07-11 native push delivery, [ADR-044](https://ivanball.github.io/docs/adr/044-native-push-delivery.html))
- **Native push pipeline (third notification channel)**: `INativePushSender` +
  `IPushDeviceRegistrar` Application abstractions with inert Null defaults, Azure Notification
  Hubs implementations (installation model, `user:{id}` tags, FCM v1 + APNs payloads, tag
  expressions OR-chunked at the hub's 20-tag cap) swapped in by
  `AddNativePushNotifications(configuration)` only when the `NativePush` section is enabled and
  complete. `SendPushNotificationHandler` gains the OS-level leg after the SignalR attempt,
  best-effort and non-fatal (new constructor parameter; DI-resolved, so hosts are unaffected).
  New `DevicesController` (PUT/DELETE `/Notifications/Devices`, any authenticated user,
  feature-gated with `Notification.PushNotifications`) ships through the existing
  `AddNotificationControllers` application part. New pin: `Microsoft.Azure.NotificationHubs`.
- **Client-side push registration capability** (ADR-042 pattern): `IPushRegistrationService` +
  `IPushDeviceTokenProvider` contracts with inert defaults in `MMCA.Common.UI`;
  `MauiPushRegistrationService` in UI.Maui (stable installation id in device preferences,
  registration synced over the API client); `PushRegistrationListener` component re-registers
  on auth-state changes; `AuthUIService.LogoutAsync` unregisters the device BEFORE clearing
  tokens (`AuthUIService` gains a constructor parameter; DI-resolved). Everything stays inert
  until the app registers a credentialed token provider (Firebase / APNs).

## [1.112.1] - 2026-07-10

### Fixed (2026-07-10 v1.112.1 OAuth allowlist null-section regression)
- **`OAuthControllerBase` no longer throws when `IConfiguration.GetSection` returns null**
  (loose configuration test doubles in consumer suites; surfaced by ADC's
  `OAuthControllerTests` failing the v1.112.0 sweep CI). A null/missing
  `OAuth:AllowedReturnUrlSchemes` section now means "empty allowlist" (the exact pre-ADR-043
  behavior); pinned by a Common-side regression test using a mocked configuration.

## [1.112.0] - 2026-07-10

### Added (2026-07-10 device-capability layer, [ADR-042](https://ivanball.github.io/docs/adr/042-device-capability-abstraction.html) / [ADR-043](https://ivanball.github.io/docs/adr/043-mobile-deep-links-and-native-oauth-callback.html))
- **`MMCA.Common.UI.Maui` (NEW, fifteenth package)**: native device-capability implementations for
  MAUI Blazor Hybrid heads over MAUI Essentials + Plugin.LocalNotification (connectivity, battery,
  share sheet, clipboard, haptics/vibration, maps launch, geolocation, system-browser links,
  text-to-speech, screen-reader announce, local notifications with tap-to-deep-link, screenshot,
  device preferences, offline JSON cache). Register with `builder.UseMauiDeviceCapabilities()`
  AFTER `AddUIShared`. The package multi-targets the four MAUI TFMs, lives outside
  `MMCA.Common.slnx`, and is built/packed by dedicated windows CI jobs (`build-maui`,
  `publish-maui`); its layer boundary (UI + Shared only) is compile-time enforced.
- **Device-capability contracts + safe defaults in `MMCA.Common.UI`**
  (`Services/Capabilities`, ADR-042): 18 per-capability interfaces with null/neutral fallbacks
  TryAdd-registered by `AddUIShared`, plus `AddBrowserDeviceCapabilities()` overrides for web heads
  (`navigator.share`/clipboard/onLine watching, aria-live announcements, localStorage preferences
  and cache via the new `capabilities-interop.js`; all prerender-safe). New shared components:
  `DeepLinkListener` (native route requests -> Blazor navigation, cold-start buffered),
  `ExternalLink` (replaces raw `target="_blank"`, which dead-ends inside a BlazorWebView), and
  `OfflineBanner` (localized en-US + es).
- **Native OAuth callback allowlist** (`OAuth:AllowedReturnUrlSchemes`, ADR-043):
  `OAuthControllerBase.CompleteAsync` can redirect the single-use completion code (and completion
  errors) to an allow-listed custom scheme (e.g. `atldevcon://oauth-complete`) so a MAUI head's
  `WebAuthenticator` flow can capture it. http(s) never matches (no open redirect); the default
  empty list preserves the previous behavior exactly.
- **`IcsCalendarBuilder` + `IcsEvent`** (MMCA.Common.Shared, `Calendars/`): dependency-free
  RFC 5545 writer (UTC-only timestamps, TEXT escaping, CRLF + 75-octet folding that never splits a
  multi-byte character, deterministic via caller-supplied DTSTAMP) for the upcoming
  add-to-calendar endpoints.

## [1.111.0] - 2026-07-10

### Fixed (2026-07-10 output-cache policy regressions, [ADR-040](https://ivanball.github.io/docs/adr/040-authenticated-output-caching-for-public-reads.html))
- **`PublicEndpointOutputCachePolicy` now varies the cache key by every query-string parameter**
  (`CacheVaryByRules.QueryKeys = "*"`, the same rule as the built-in default policy). The v1.110.0
  policy replaced the whole default-policy chain, so it silently dropped query variance: every
  search, paging, filter, and field-projection variant of a path shared ONE cache entry, serving
  whichever response populated first (surfaced as ADC/Store integration + E2E gate failures on
  the v1.110.0 sweep deploys, e.g. a no-ids `variant-lookup` returning another test's cached
  non-empty payload and grid reads returning wrong pages).

### Added (2026-07-10 output-cache bypass roles)
- **`AddPublicEndpointPolicy(name, expiration, bypassRoles, tags)` overload** (and the matching
  `PublicEndpointOutputCachePolicy(expiration, bypassRoles, tags)` constructor): callers in a
  bypass role skip the output cache entirely (no lookup, no storage) and always read fresh. Use
  for `[AllowAnonymous]` endpoints whose payload is identical for every caller EXCEPT a privileged
  role receiving an elevated payload (e.g. ADC organizers see unpublished events per BR-108).
  Without this, an elevated response could be cached and served verbatim to anonymous callers.

## [1.110.0] - 2026-07-10

### Changed (2026-07-10 notification inbox live refresh)
- **`NotificationInbox` reloads on real-time push**: the inbox page now subscribes to
  `NotificationState.OnRefreshRequested` (the same signal `NotificationListener` raises on every
  SignalR `ReceiveNotification`) and reloads its current page, so an open inbox shows a new
  notification without navigation. Previously a push only produced a toast and a bell-badge bump.
  Overlapping refreshes coalesce (a push arriving mid-load is skipped; the next push or
  navigation reconciles).

### Added (2026-07-10 runtime performance wave, [ADR-040](https://ivanball.github.io/docs/adr/040-authenticated-output-caching-for-public-reads.html))
- **`PublicEndpointOutputCachePolicy` + `OutputCacheOptions.AddPublicEndpointPolicy(name, expiration, tags)`**
  (MMCA.Common.API): output-cache policy for `[AllowAnonymous]`, user-independent GET endpoints
  that caches despite an `Authorization` header. The UI attaches a Bearer token to every request,
  so the built-in default policy served logged-in users a 0% cache hit rate and every public read
  landed on the database; see ADR-040 for the strict apply-only-to-identity-independent contract.
- **`HttpResilienceDefaults`** (MMCA.Common.Shared.Resilience): single source of truth for the
  outbound-HTTP resilience and socket-handler values shared by `MMCA.Common.Aspire` and
  `MMCA.Common.Grpc` (the two hand-mirrored copies had drifted). MMCA.Common.Aspire now
  references MMCA.Common.Shared.

### Changed (2026-07-10 runtime performance wave)
- **BREAKING: `IEntityQueryService` shaped payloads widened from `ExpandoObject` to `object`**
  (`GetAllAsync` returns `Result<PagedCollectionResult<object>>`, `GetByIdAsync` returns
  `Result<object>`). When no `fields` subset is requested (the overwhelming majority of list/read
  traffic), the typed DTOs are now returned as-is instead of being reshaped into one
  `ExpandoObject` per row (per-row allocation + boxing + slower dictionary serialization on 100%
  of list GETs). Explicit `fields` selections still shape. The wire format is unchanged (same
  camelCase JSON); consumers only need mechanical retyping where they name the old generic
  (typically controller-test mocks). Note: DTO `[JsonPropertyName]`/`[JsonIgnore]` attributes are
  now honored on unshaped responses (the Expando path ignored them); no shipped DTO relied on that.
- **Outbox mark-processed is set-based and fully async**: after in-process dispatch,
  `DomainEventSaveChangesInterceptor` (and `InProcessEventBus`) stamp `ProcessedOn` with one
  `ExecuteUpdateAsync` instead of a nested synchronous `SaveChanges()`. This removes a blocking
  thread-pool DB round trip (plus a full re-entrant interceptor pipeline) from every
  event-raising command in every consumer. `InProcessEventBus` batch publishes now persist all
  events in ONE save and dispatch in one call (previously 2 round trips per event); a dispatch
  failure leaves the whole batch for the outbox processor (at-least-once, inbox dedup unchanged).
- **`Result` success path is allocation-free**: the error list is lazily allocated (every Result
  previously allocated a `List<Error>` even on success) and `Result.Success()` returns a shared
  instance. **`Result`/`Result<T>` are now JSON round-trippable** via an attribute-applied
  converter factory (`{"value": ..., "errors": [...]}` shape): required by the distributed query
  cache, where a Redis hit previously could not rehydrate (internal ctors, get-only `Value`) and
  the in-memory fallback masked it.
- **`CachingQueryDecorator` gained stampede protection**: per-cache-key double-check locking (the
  `IdempotencyFilter` pattern) so concurrent misses on a hot expired key run the handler once.
- **`DistributedCacheService`**: prefix invalidation now deletes keys in batches of 512 (one
  Redis round trip per batch instead of one per key, sequential, on the request thread);
  serialization uses `SerializeToUtf8Bytes` (drops a full buffer copy per cache write).
- **Retry ownership bounded**: the standard resilience handler applied to every factory client by
  `AddServiceDefaults()` now retries ONCE (was 3): the UI service base classes own user-facing
  retries, and stacked budgets amplified a backend brownout up to 16x. `AddTypedGrpcClient`'s
  resilience options now mirror the Aspire defaults exactly (they had silently drifted to the
  10s/30s library defaults) and its `SocketsHttpHandler` regains `PooledConnectionLifetime` +
  keep-alive pings (gRPC connections were never recycled, pinning stale ACA replicas after scale
  events).
- **`EFReadRepository.ApplyIncludes` opts into split query when any string include targets a
  collection navigation** (cached reflection walk), mirroring the query pipeline's heuristic:
  sibling collection includes on the direct repository path multiplied rows via JOIN products.
- **`LoggingCommandDecorator`**: the started line dropped to Debug (the completion line already
  carries name + duration; two Information rows per command doubled ingestion) and the logging
  scope is source-generated (`LoggerMessage.DefineScope`) instead of a per-command dictionary.
- **Reflection off hot paths**: `DomainEventDispatcher` caches the closed handler type beside the
  compiled invoker (no `MakeGenericType` per dispatch); `ResultFailureFactory` compiles the
  generic failure constructor once per closed type (no `MethodInfo.Invoke` per short-circuit);
  `OutboxMessage.DeserializeEvent` caches `Type.GetType` lookups and now deserializes with the
  same cycle-ignoring options used to serialize; `EFRepository.UpdateAsync` uses the tracker's
  O(1) `LocalView.FindEntry` instead of scanning the local view.
- **Gzip response compression level moved from `SmallestSize` to `Fastest`** (Brotli already
  `Fastest`): dynamic API payloads on fractional vCPUs.

## [1.109.0] - 2026-07-10

### Changed (2026-07-09 domain rejection messages in error toasts, [ADR-027](https://ivanball.github.io/docs/adr/027-multi-locale-i18n.html) Decision 9 carve-out)
- **`ErrorMessages.LoadError/SaveError/DeleteError` surface a `DomainInvariantViolationException`
  message verbatim** in place of the generic "Error loading/saving/deleting {0}." template, and the
  new **`ErrorMessages.ActionError(ex, localizedFallback)`** does the same for pages whose fallback
  is a whole-sentence snackbar key of their own resource pair. `ServiceExceptionHelper` mints that
  exception type exclusively from the API's Problem Details errors, whose text is curated domain
  wording already localized server-side to the request culture (Accept-Language via
  `CultureDelegatingHandler`), so users now see the actual business rule that rejected an action
  ("This action is only available while the event is live.") instead of a generic failure toast.
  Behavior change, not a breaking one: no signatures moved, and every other exception type still
  gets the generic localized message (raw exception text is still never shown, ADR-027 Decision 9).

## [1.108.0] - 2026-07-09

### Added (2026-07-09 live channels, [ADR-039](https://ivanball.github.io/docs/adr/039-live-channel-push.html))
- **Ephemeral live channel events over the existing notification hub**: `NotificationHub` gains its
  first client-invokable methods, `JoinChannel` / `LeaveChannel` (SignalR group membership; channel
  keys validated against the new `PushNotificationSettings.ChannelKeyPattern`, default
  `^(event|session):[0-9]+$`, invalid keys rejected with `HubException`), plus a
  `ReceiveChannelEvent` client method. A new `ILiveChannelPublisher` abstraction (Application,
  beside `IPushNotificationSender`) publishes `(channelKey, eventName, payloadJson)` to a channel;
  `SignalRLiveChannelPublisher` delivers via group send, and the no-op `NullLiveChannelPublisher`
  is the default registration, swapped by `AddPushNotifications()` (the ADR-024 pattern).
  `NotificationHubService` (UI) gains `JoinChannelAsync` / `LeaveChannelAsync` and multicast
  `OnChannelEvent` subscriptions on the existing connection, and re-joins tracked channels
  automatically after an automatic reconnect (SignalR group membership does not survive one).
  Fully additive: the `IPushNotificationSettings` interface and all existing notification
  behavior are unchanged.

## [1.107.0] - 2026-07-07

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.106.0] - 2026-07-05

### Fixed (2026-07-05 defect-fix wave C-1..C-5)
- **`LoginProtectionService` lockout backoff no longer overflows on deep failure counts** (C-1,
  security): the shift exponent is clamped to 30, so 31 or more excess failed attempts keep the
  `MaxLockoutSeconds` cap instead of computing a negative or wrapped-back-to-seconds lockout TTL
  (C# masks int shift counts to 5 bits).
- **`OAuthControllerBase.CompleteAsync` no longer throws when the ticket has no returnUrl** (C-2):
  an external-login ticket whose `AuthenticationProperties` omits the `returnUrl` item now completes
  with the `/` fallback instead of failing the whole OAuth flow with `KeyNotFoundException`.
- **Query metrics no longer count business failures as completed** (C-3): `LoggingQueryDecorator`
  inspects the returned `Result` the same way the command decorator does, so `cqrs.query.duration`
  records `outcome=failed` for `Result.IsFailure` (and logs a warning with the error summary)
  instead of conflating failures with successes.
- **`ChildEntityServiceBase` now attaches the JWT Bearer token to its requests** (C-4,
  **consumer-breaking**): it derives from `AuthenticatedServiceBase` and its constructor now
  requires an `ITokenStorageService` between the `IHttpClientFactory` and the endpoint, which
  subclasses must pass through. Previously every join-entity POST/DELETE was sent anonymously and
  failed against `[Authorize]` endpoints; consumer subclasses must add the parameter in the same
  release sweep.
- **`EntityServiceBase.GetAllForLookupAsync` escapes `nameProperty`** (C-5): a space, ampersand, or
  other reserved character in the lookup property name is now percent-encoded (the same treatment
  the paged path gives its sort/filter parameters) instead of corrupting the query string.

### Changed (2026-07-05 TimeProvider extension points C-6/C-7)
- **`OutboxCleanupService` gains an optional trailing `TimeProvider` constructor parameter** (C-6,
  non-breaking, defaults to `TimeProvider.System`): the hour-scale sweep interval and the retention
  cutoff run on the injectable clock, making the purge sweep deterministically unit-testable with
  `FakeTimeProvider` (which the new sweep tests do).
- **`SessionCookieAuthenticationHandler` checks JWT expiry against the handler's `TimeProvider`**
  (C-7) instead of `DateTime.UtcNow`; no constructor change (set `options.TimeProvider` in tests
  for a deterministic clock).

## [1.105.2] - 2026-07-04

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.105.1] - 2026-07-04

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.105.0] - 2026-07-04

### Added (2026-07-04 user-preferences E2E base, §14/§27/§28)
- **`UserPreferencesTestsBase`** (`MMCA.Common.Testing.E2E`, `Workflows.Preferences`): three
  self-contained facts consumers inherit with a one-line subclass: Spanish culture switch with
  cookie persistence across reload (probed via the shared /login "Welcome Back" heading), dark-mode
  toggle asserting the emitted `--mud-palette-background` variable flips to the PaletteDark value
  and persists across reload, and a 390px-viewport fact pinning the v1.103.0 mobile top-row
  controls in real apps (not just the gallery). No app-specific overrides needed.

## [1.104.2] - 2026-07-04

### Fixed (2026-07-04 logout-then-login race, remaining site)
- **`ProfileManagementTestsBase.ChangePassword_WithValidCurrentPassword_ShouldSucceed` is now
  navigation-safe**: it waited on `LoadState.Load` after the sign-out click (already fired for the
  current document), so the re-login raced the in-flight logout forceLoad and died with
  `net::ERR_ABORTED` / "interrupted by another navigation" on contended runners (deterministic on
  Store's v1.104.1 e2e-gate). Now waits for the `/login` URL, the same fix v1.103.1 applied to
  `UserLoginTestsBase`; this was the one remaining sign-out-then-login site on the racy pattern.

## [1.104.1] - 2026-07-04

### Fixed (2026-07-04 warning-chip contrast, §20/§22)
- **Filled Warning components now meet WCAG 2.1 AA in both palettes** (`MMCATheme`): MudBlazor's
  default white contrast text is ~2.65:1 on the light palette's `#F57F17` (and ~2.0:1 on the dark
  palette's `#FFA726`); `WarningContrastText` is now dark in both palettes (~7.9:1 / ~10.8:1, the
  standard Material treatment on amber). Latent until Store's new Buy Now E2E put a "Pending
  Payment" chip on the gated admin-order-list axe scan. Visual change: warning chips/buttons
  render dark-on-amber instead of white-on-amber.

## [1.104.0] - 2026-07-04

### Added (2026-07-04 E2E authorization depth, §14)
- **`AuthorizationTestsBase.AdminPaths` + `RegisteredUser_AdminPages_ShouldBeForbidden`**
  (`MMCA.Common.Testing.E2E`): consumers declare their admin-only routes and the shared base verifies a
  freshly-registered regular user gets the shared Forbidden page ("Access Denied") on each: the
  escalation direction the anonymous-redirect test cannot cover. Empty default keeps apps without an
  admin surface passing unchanged.

### Changed (2026-07-04 E2E authorization depth, §14)
- **`ProfileManagementTestsBase.ChangeEmail_ShouldUpdateEmail` no longer probes the DOM** to decide
  whether email change exists (that made it pass vacuously on apps whose profile page has no email
  section). It is now gated by the new `ProfileSupportsEmailChange` opt-in (default false); a consumer
  that opts in gets a loud failure when the email field goes missing. No consumer opts in today, so
  observed behavior is unchanged: the test's silence is now declared instead of accidental.

## [1.103.1] - 2026-07-04

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.103.0] - 2026-07-04

### Fixed (2026-07-04 mobile parity)
- **Culture + theme controls are now reachable on phones (§22 / ADR-027/028).** The shared layout
  hides the whole `MudAppBar` below 1024px, and the `CultureSwitcher`/`ThemeToggle` lived only there,
  so no phone user (anonymous or signed-in) could switch language or theme. `NavMenu`'s mobile
  top-row now renders both controls unconditionally (module app-bar components and the user name
  stay auth-gated); existing top-row CSS handles compact sizing, white icons, and the desktop hide,
  so nothing renders twice. Pinned by `MobileTopRowE2ETests` (phone + desktop viewports) in the
  required chromium `ui-e2e` job.

## [1.102.0] - 2026-07-04

i18n completion train (ADR-027 amended 2026-07-03, §27): every remaining user-visible literal in the
shared UI is externalized, MudBlazor chrome localizes, and two new gates keep it that way.
**Consumer-breaking on purpose:** `ErrorMessages.Success(entity, action)` is now `[Obsolete]`
(a warning, which consumer `TreatWarningsAsErrors` promotes to a build error), forcing the same-pass
sweep to whole-sentence page resource keys per ADR-016's lockstep rule.

### Added (2026-07-03 i18n completion)
- **`ResxMudLocalizer` + `MudTranslations.{resx,es.resx}`** (`MMCA.Common.UI`): MudBlazor built-in
  component text (data-grid pager and filter menus, pickers, table editing, pagination, close buttons)
  now follows the active culture; all built-in keys of the pinned MudBlazor version ship en + es.
  Registered in `AddUIShared` via `TryAddTransient<MudLocalizer, ResxMudLocalizer>` (guarded by a DI
  resolution test).
- **`LocalizedTextConventionTestsBase` + `ArchitectureRules.UserVisibleTextIsLocalized`**
  (`MMCA.Common.Testing.Architecture`, now 78 methods / 25 bases): fails the build on hard-coded
  snackbar literals, literal page `Title` properties, literal `<PageTitle>` markup, literal breadcrumb
  labels, and `NavItem` rows without a `TitleResource`; per-line `i18n: allow` marker for deliberate
  literals (brand names). Subclass in every repo.
- **Pseudo-localization CI gate** (`PseudoLocalizationE2ETests`, required chromium `ui-e2e` job): the
  gallery renders `/login`, `/register`, `/components` under `qps-Ploc` and asserts the bracket
  sentinel appears and no horizontal overflow occurs under the ~40% expansion (rubric §27 layout
  tolerance); an `en-US` leak-guard asserts the sentinel never ships to a real locale.
- **`NavItem.TitleResource`** (optional, defaulted): when set, the shared `NavMenu` resolves
  `Title`/`Group` as resource keys per circuit so module nav menus follow the culture; literal-titled
  items render unchanged.
- **Fully localized shared chrome** (`SharedResource.{resx,es.resx}`, 136 keys): NavMenu, Login,
  Register, OAuthComplete, Forbidden, NotFound, Home fallback, Counter, the notification pages
  (titles, breadcrumbs, table headers, status chips, form labels), ReconnectModal, EmptyState,
  PageErrorState, PageLoadingState, DeleteConfirmation, UnsavedChangesGuard, MobileCardList,
  MobileInfiniteScrollList, and the `UI.Web` SSR Error page.

### Changed (2026-07-03 i18n completion)
- **`Common.Error.Load/Save/Delete` resource values no longer append raw `ex.Message`** (neither
  localizable nor safe to surface); method signatures are unchanged, extra format args are ignored.
- **Component parameter defaults localize** (`UnsavedChangesGuard`, `PageLoadingState`, `EmptyState`,
  `PageErrorState`, `DeleteConfirmation`, `MobileCardList`, `MobileInfiniteScrollList`): the affected
  string parameters are now nullable with localized fallbacks; explicit consumer values still win.
- **The shared Register page subtitle is generic** ("Create your account to get started"): the
  previous literal was ADC-conference copy leaking into every consumer of the shared page.
- **`LocalizationResourceTests` (Common) sets `MinimumBaseResources = 3`** so the completeness gate
  can no longer pass vacuously.

### Deprecated (2026-07-03 i18n completion)
- **`ErrorMessages.Success(entity, action)`**: composed sentences cannot be translated (Spanish gender
  agreement breaks). Use a whole-sentence key in the page's own resource pair, e.g.
  `Snackbar.Add(L["Snackbar.Created"], ...)`.

## [1.101.0] - 2026-07-03

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.100.0] - 2026-07-02

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.99.0] - 2026-07-02

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.98.0] - 2026-07-01

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.97.0] - 2026-07-01

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.96.0] - 2026-07-01

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.95.0] - 2026-07-01

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.94.0] - 2026-06-30

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.93.0] - 2026-06-30

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.92.0] - 2026-06-29

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.91.0] - 2026-06-29

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.90.0] - 2026-06-28

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.89.0] - 2026-06-28

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.88.0] - 2026-06-28

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.87.0] - 2026-06-28

_No entries were recorded at release time; see the git tag for this release's changes._

## [1.86.0] - 2026-06-27

Internationalization (ADR-027) + Day/Dark theme mode (ADR-028), plus maturity-axis remediation (§29, §30)
and DDD fitness hardening (§4). No breaking changes (the static `ErrorMessages` signatures are preserved).

### Added
- **Multi-locale i18n (ADR-027, supersedes ADR-011).** Framework now supports `en-US` + Spanish (`es`):
  - **Edge error localization keyed by `Error.Code`.** `IErrorLocalizer` (`MMCA.Common.API/Localization`)
    translates the human-readable message at the HTTP edge (`ErrorHttpMapping.BuildErrorsExtension`,
    applied in `ApiControllerBase.HandleFailure` + `UnhandledResultFailureFilter`), falling back to the
    English `Error.Message` for any unmapped code; the ProblemDetails `title` and the `Code`/`Source`/
    `Target` stay verbatim. Common ships `ErrorResources.{resx,es.resx}`; modules add their own via
    `AddErrorResources<TResource>()`. Wired automatically by `AddAPI` (`AddErrorLocalization`).
  - **Request localization + culture switch.** `UseCommonRequestLocalization()` (in the shared service
    pipeline) and `MapCultureEndpoint()` (`GET /culture/set`) plus a `SupportedCultures` allowlist
    (`MMCA.Common.Shared`). UI: `AddUIShared` registers `AddLocalization()` + a `CultureDelegatingHandler`
    that forwards the active culture as `Accept-Language`; a `CultureSwitcher` component; and
    `MmcaCultureBootstrap.SetBrowserCultureAsync` for the WASM `.Client` to match SSR (no locale flash).
  - **Localized shared chrome.** `MainLayout`, `ErrorMessages`, and `DataGridListPageBase` snackbars now
    resolve from `SharedResource.{resx,es.resx}` via `IStringLocalizer`.
- **Day/Dark theme mode (ADR-028).** `MudThemeProvider` is now bound (`@bind-IsDarkMode`) to the existing
  `MMCATheme.PaletteDark`; a `ThemeService` persists the choice to a cookie + localStorage (default = OS
  `prefers-color-scheme`) and a `ThemeToggle` ships in the shared app bar beside the culture switcher.
- **In-repo restore-drill smoke (§29).** `DatabaseRestoreDrillTests`
  (`Tests/Core/MMCA.Common.Infrastructure.Tests/Resilience/`) exercises the full recovery procedure —
  seed → backup → simulated catastrophic data loss → restore → verify zero data loss, timing the RTO —
  against an ephemeral SQLite database via the SQLite online-backup API. The framework now demonstrates
  the restore *procedure* centrally instead of only inheriting it downstream; `RESILIENCE.md` records the
  baseline.
- **Non-vacuous PII erasure-contract fitness (§30).** `PiiErasureContractFitnessTests` forces a
  representative `[Pii]`-carrying data subject through `PiiRedactor` (masking + no clear-text leak) and
  `IAnonymizable` (idempotent in-place erasure), proving the three §30 mechanisms compose end to end —
  closing the "no fitness function forces a type through the redactor" gap (ADR-005).
- **Aggregate private-constructor fitness rule (§4).** `AggregateConventionTestsBase` now also asserts
  Domain-layer aggregate roots expose no public constructor (construction goes through the static
  `Create(...)` Result-factory) via `ArchitectureRules.DomainAggregateRootsHaveNoPublicConstructors` —
  the minimal-base counterpart to the module-scoped rule, so the framework's own aggregates are covered
  (now 71 fitness methods / 18 bases).

## [1.85.0] - 2026-06-27

Under-8 Implementation remediation: every architecture-scorecard category scored Implementation < 8
is lifted with shipped, tested evidence (reference samples + real code levers). No breaking changes.

### Added
- **Slice-cohesion fitness function (§5).** `SliceCohesionTestsBase` + `ArchitectureRules.Slices`
  in `MMCA.Common.Testing.Architecture` (now 70 methods / 18 bases) — fails the build if a
  use-case slice's handler/validator is stranded from its same-assembly command/query contract.
  Re-run as a thin subclass in every repo.
- **OTel trace sampler knob (§31).** `Telemetry:TracesSampleRatio` (a value in `(0,1)`) installs a
  `ParentBasedSampler(TraceIdRatioBasedSampler)` in `AddServiceDefaults`; unset = sample everything.
  The biggest lever on trace-ingestion cost.
- **In-shell 403 page (§25).** `Pages/Forbidden.razor` rendered for the authenticated-but-unauthorized
  route branch (was a bare alert), plus `NavigationFlow.md` documenting the Common UI route/role model.
- **Reference deployment sample (§17).** `samples/deployment/{foundation,main}.bicep` (Container Apps
  + ACR-via-managed-identity + Key Vault + SQL + cost tags + budget) + `DEPLOYMENT.md` (OIDC + UAMI
  bootstrap + smoke-gate/auto-rollback).
- **`RESILIENCE.md` (§29)** — baseline SLO/error-budget template + restore-drill runbook reference;
  the warm-up readiness subsystem is now unit-tested.
- **BenchmarkDotNet smoke harness (§12)** — `Tests/Performance/MMCA.Common.Benchmarks` (outside the
  `.slnx`); hot-path spec efficiency is now measured, not assumed.

### Changed
- **Register/Login use `EditForm` + DataAnnotations field-level validation (§24)** — errors are tied
  to the offending input (`ValidationMessage`) with the summary kept for form-level/server errors.
- **Outbox per-message "dispatched" log moved Information → Debug (§31)** — the highest-volume log
  line in steady state; failures stay loud (dead-letter = Error, retry = Warning).
- **`COST.md`** gains cost-attribution-tag + cost-guard-workflow samples and documents the sampler knob.

## [1.84.0] - 2026-06-27

PII log/telemetry redaction (§30). No breaking changes.

### Added
- **`PiiRedactor` (§30).** `Domain/Privacy/PiiRedactor.cs` masks every `[Pii]`-marked member (shallow,
  value-erasing `[REDACTED]` token, per-type reflection cache) before an entity carrying personal data
  reaches a structured log or telemetry attribute — the redaction half of the `[Pii]` contract (ADR-005),
  complementing the `IAnonymizable` erasure extension point. Covered by `PiiRedactorTests` (incl. "never emits the
  clear-text PII values").

## [1.83.0] - 2026-06-26

Governance + front-end security hardening. No breaking changes.

### Added
- **ADR-023 — centralized security-response headers (§26).** Documents the hardened security-headers
  middleware + pluggable `ICspPolicyProvider` CSP extension point (`AddCommonSecurityHeaders`), replacing per-host
  hand-rolled headers.
- **Source-generated, CI-gated `FACTS.md` (§34).** `build/facts` computes version / package-count /
  ADR-range / fitness counts from source; the `build-and-test` job runs it with `--check` and fails the
  build on drift, so the framework facts are a computed-and-gated artifact rather than hand-maintained prose.
- **Canonical two-axis `ArchitectureScorecard.md` (§34).** The rubric (`ArchitectureEvaluationCriteria.md`)
  and scorecard are version-controlled in-repo (mirroring the ADR governance pattern).

## [1.82.0] - 2026-06-26

Governance + supply-chain + E2E-stability hardening. No breaking changes.

### Security
- **RS256 pinned on the JWKS-forwarded auth path.** `ValidAlgorithms = [RsaSha256]` on the
  forwarded-JWT (JWKS discovery) validation path in `MMCA.Common.API` — defense-in-depth against an
  algorithm-confusion swap, matching the existing in-process pin.

### Added
- **ADRs 020-022** — 020 (permission-based authorization), 021 (consumer inbox idempotency),
  022 (browser session-cookie auth); the committed ADR set is now 001-022.

### Fixed
- **Lock drift.** Pinned the transitive `Deque.AxeCore.Commons` to 4.12.0 in
  `MMCA.Common.Testing.E2E` so a stale-cache restore no longer drifts it to 4.7.2 and dirties the lock.

### Internal
- **E2E register/login de-flake (R11).** `RegisterNewUserAsync`/`LoginAsync` now give the success
  signal a grace window (`E2ETestConfiguration.AuthGraceTimeout`, default 15s, `E2E_AUTH_GRACE`) when a
  transient error alert flashes during the success-path `forceLoad` — only a persistent error is a real
  failure. Detection-only (cannot break auth), unlike the reverted WASM-forcing.

## [1.81.0] - 2026-06-26

Post-v1.80.0 polish: an opt-in OpenAPI UI, FinOps documentation, and test-coverage hardening for the
v1.80.0 rate-limiter and `TimeProvider` extension points. Additive — no breaking changes and no consumer behavior
change beyond the new opt-in helper.

### Added
- **Scalar API-reference UI helper (opt-in, §9).** `MapCommonScalarUi()` (`MMCA.Common.API`) renders the
  generated OpenAPI document as an interactive reference at `/scalar/{documentName}`, **outside Production
  only**. Opt-in (a host calls it explicitly); assets are served by the bundled `Scalar.AspNetCore` package
  (no external CDN). Pairs with `AddCommonOpenApi()` / `MapCommonOpenApi()`. Internal services behind the
  Gateway need not call it — it's for hosts run standalone where a rendered reference helps.
- **`COST.md` (FinOps notes, §31)** — consolidates the framework's cost-relevant defaults (telemetry
  poll-span filtering, outbox poll/retention tuning) and the right-sizing / attribution / surge-revert
  levers consumers set downstream.
- **ADR-019 (layered rate limiting)** documents the authenticated-only global limiter (shipped earlier),
  bringing the committed ADR set to **001-019**; the CHANGELOG was also backfilled for 1.72.0-1.80.0.

### Changed
- **`MMCA.Common.API` takes a new dependency on `Scalar.AspNetCore`** (MIT) for the optional UI helper
  above. Consumers referencing `MMCA.Common.API` pull it transitively; it has no runtime effect unless
  `MapCommonScalarUi()` is called.

### Internal
- Rate-limiter exemption/partition helpers are now `internal` (via `InternalsVisibleTo`) and unit-tested
  (`RateLimitPartitionTests` — bypass paths, anonymous-vs-authenticated, per-user partition-key fallback).
- The two notification read-handler tests now assert the stamped read-time against a fixed `TimeProvider`.
- `BaseDomainEvent.DateOccurred`'s creation-time default is documented as a deliberate occurrence-time
  choice (event-sourcing / audit semantics), not changed.

## [1.80.0] - 2026-06-25

Opt-in permission-based authorization plus `TimeProvider` adoption on the time-sensitive paths.

### Added
- **Permission-based authorization (opt-in).** `IPermissionRegistry` + `PermissionRegistryBuilder`
  (`MMCA.Common.Shared`) declare role→permission grants; `[HasPermission("x")]` (`MMCA.Common.API`)
  resolves an on-demand `perm:x` policy via `PermissionPolicyProvider`, and
  `PermissionAuthorizationHandler` grants access when the caller carries an explicit `permission`
  claim or holds a role the registry grants it. Wired through `AddAuthorizationPolicies` +
  `AddPermissions(...)`. **Backward-compatible** — the existing named role policies are untouched and
  the mechanism is inert until a host calls `AddPermissions`. It is RBAC with a role→permission
  capability indirection (policy-based, not resource/attribute-based).
- **`RoleNames.ContentEditor`** — a granular conference-content role consumers can grant a permission
  subset (used by ADC).
- **ADR-019 (layered rate limiting)** documents the always-on, authenticated-only global limiter
  (`AddCommonRateLimiting`): infrastructure traffic (`/health`, `/alive`, `/.well-known/*`,
  `application/grpc`) and anonymous requests are exempt; authenticated callers are capped per principal.
  The limiter code itself is pre-existing — this release adds the decision record. ADRs 017/018 are
  also now committed (idempotency, polyglot persistence).

### Changed
- **`TimeProvider` adoption.** `TokenService` (token `iat`/`nbf`/`exp`) and the notification read
  handlers now derive time from an injected `TimeProvider` instead of `DateTime.UtcNow`;
  `UserNotification.MarkAsRead(DateTime readOnUtc)` takes an explicit UTC timestamp, keeping the
  aggregate free of ambient clock access. Non-breaking — `TimeProvider.System` is the default.

## [1.79.0] - 2026-06-24

Polyglot-persistence ergonomics: moving an entity between data-source engines becomes a minimal,
build-guarded change (ADR-006).

### Added
- **Unified entity configuration base.** `EntityTypeConfiguration<TEntity, TIdentifierType>`
  (`MMCA.Common.Infrastructure`) declares an entity's target engine via a `[UseDataSource]` attribute
  and applies the matching table/container/key conventions. `EntityTypeConfigurationSQLServer`,
  `…Sqlite`, and `…Cosmos` are now thin attribute-carrying shims over it, so changing an entity's engine
  is a one-token base-class (or attribute) change with no configuration-body edits.
- **Cosmos / SQLite AppHost wiring.** `WithCosmosDataSource(...)` and `WithSqliteDataSource(...)`
  Aspire.Hosting extensions (alongside the SQL Server helper) for routing a module to a polyglot data
  source.
- **Cross-source specification helper.** `CrossSourceSpecification.BuildAsync(...)` plus
  `InlineSpecification` build a translatable `localPredicate AND foreignKey IN (resolved keys)` filter
  for a dependent entity whose principal lives in a different physical data source — where a navigation
  join is not translatable (e.g. a Cosmos dependent and a SQL Server principal).
- **Specification fitness rule (opt-in).** `ArchitectureRules.SpecificationsDoNotNavigateToOtherEntities`
  + `SpecificationConventionTestsBase` (`MMCA.Common.Testing.Architecture`) fail the build when a
  specification's `Criteria` navigates to another entity — a latent cross-source hazard in a
  database-per-service / polyglot setup. Polyglot-capable repos opt in; single-engine repos need not.

### Changed
- **(Breaking)** Renamed the Aspire.Hosting extension `WithDataSource` → **`WithSQLServerDataSource`**
  for `With*DataSource` naming consistency with the new Cosmos/SQLite helpers. Consumers update their
  AppHost calls to `service.WithSQLServerDataSource(db, "Module")`.

### Fixed
- **Cosmos config-body portability.** `CrossDataSourceDegradeConvention` no longer adds a compensating
  index when degrading a cross-source foreign key in a **Cosmos** context — the Cosmos provider rejects
  index definitions, so the re-added index previously failed model validation. A configuration body that
  keeps a cross-source relationship (or a filtered index) is now portable to Cosmos unchanged.
- **SQLite schema under the `"Migrate"` strategy.** `DatabaseInitializationExtensions` now
  `EnsureCreated`s SQLite sources (which have no EF migrations) up front, independent of the
  SQL-Server-oriented strategy; previously a SQLite source in use was never created under `"Migrate"`
  (or `"None"`) and the first repository call failed.
- **Cosmos container naming.** `EntityTypeConfigurationCosmos` derives the container from the module
  namespace segment preceding `Domain` (the same rule as the SQL schema / logical database name); it
  previously looked for a `Modules` segment that the actual namespaces do not contain, falling back to a
  per-type container.

## [1.78.0] - 2026-06-23

### Changed
- Upgraded NuGet dependencies to their latest stable versions (held packages — MassTransit v8,
  `Microsoft.VisualStudio.Threading.Analyzers`, `StackExchange.Redis`, `MessagePack` — excluded per
  the semver-major Dependabot ignores and the MassTransit-v8 license pin).

### Added
- **`GETTING-STARTED.md`** — a 9-phase framework-adoption guide (solution plumbing → a module vertical
  slice → Aspire host → architecture-fitness map → a worked module extraction), with MMCA.Helpdesk as
  its runnable companion.

## [1.77.0] - 2026-06-22

### Added
- **ADRs 013-016** — Result pattern (013), CQRS decorator pipeline (014), architecture fitness
  functions (015), lockstep versioning + MassTransit-v8 pin (016) — documenting patterns that
  previously lived only in code. Docs-only release.

## [1.76.0] - 2026-06-22

### Changed
- Documentation alignment with the workspace `Docs/` folder reorganization. Docs-only release.

## [1.75.0] - 2026-06-21

### Changed
- Drift-reduction housekeeping (D5/D22/D29/D30): consolidated the exception-handler tests, lifted the
  shared E2E scan helpers, documented the MassTransit-pin boundary (enforced only in MMCA.Common; ADC
  and Store inherit it transitively), and reconciled ADR-012.
- README now links the architecture scorecard + ADRs and lists all 13 packages.

## [1.74.0] - 2026-06-21

### Changed
- **Promoted shared cross-cutting infrastructure up into MMCA.Common** (drift-reduction P4) so the
  consumer apps inherit it instead of carrying parallel copies.

## [1.73.0] - 2026-06-21

### Added
- **`MMCA.Common.Testing.Architecture`** package (the **13th**) — define-once architecture fitness
  functions: an `IArchitectureMap`-parameterized NetArchTest rule library + abstract test bases, so
  MMCA.Common, Store, and ADC run the *same* rules as thin subclasses rather than parallel copies
  (ADR-015).

### Fixed
- Reverted the v1.72.0 "force WASM interactivity before auth submit" E2E change (it caused a CI
  regression); fixed a CI-only IDE0370 null-forgiving analyzer error in the new arch-test package.

## [1.72.0] - 2026-06-20

### Added
- **Resilience fault-injection test** (`ResilienceCircuitBreakerFaultInjectionTests`) that trips a Polly
  circuit breaker and asserts short-circuiting, plus an outbox/inbox dedup test (tests only).

## [1.71.0] - 2026-06-19

### Added
- **Broker retry policy.** `AddBrokerMessaging` now configures `UseMessageRetry` (exponential backoff)
  on both the RabbitMQ and Azure Service Bus transports. Tunable via new `MessageBus:RetryLimit`,
  `MessageBus:RetryMinIntervalSeconds`, and `MessageBus:RetryMaxIntervalSeconds` settings.
- **`IAnonymizable`** erasure extension point (`MMCA.Common.Domain`) for reconciling soft-delete with
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

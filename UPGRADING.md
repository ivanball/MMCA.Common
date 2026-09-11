# Upgrading

Breaking changes in MMCA.Common ship as **MINOR** bumps (see the
[versioning policy](https://ivanball.github.io/docs/guides/common-VERSIONING.html)). This file is
the durable companion to [CHANGELOG.md](CHANGELOG.md): one section per release that breaks a
consumer, newest first, holding the full old-to-new map and the mechanical fix. The changelog entry
for the same release carries the same map under its bold **Breaking:** line; the two are kept
identical.

There is no dual-namespace grace release and no `[Obsolete]` shim for a namespace move: C# cannot
forward a type across namespaces inside one assembly, so the old namespaces stop existing in the
release that introduces the new ones. Pin an exact version and upgrade deliberately.

## The mechanical fix for a namespace move

1. Replace every `using OldNamespace;` (also `global using` and Razor `@using`) with the `using`
   line of **each** successor namespace listed for it below. Adding all successors is safe: the
   build then reports the ones a file does not need as IDE0005, and `dotnet format` (or the
   compiler's fixer) removes them.
2. Search for fully qualified references (`OldNamespace.TypeName`) in code, doc comments and
   `nameof` expressions and re-qualify them.
3. Rebuild. Every remaining error is a `using` a file gained through the old namespace implicitly
   (a type that used to sit beside it); add the successor `using` the error names.

Example for one namespace, from a POSIX shell at the consumer's root:

```sh
grep -rl --include='*.cs' --include='*.razor' 'using MMCA.Common.Application.UseCases;' . \
  | xargs sed -i 's/^\(\s*\)\(global \)\?using MMCA.Common.Application.UseCases;/\1\2using MMCA.Common.Application.UseCases;\n\1\2using MMCA.Common.Application.UseCases.Contracts;\n\1\2using MMCA.Common.Application.UseCases.Markers;\n\1\2using MMCA.Common.Application.UseCases.Crud;/'
```

The first-party consumers (MMCA.ADC, MMCA.Store, MMCA.Helpdesk) are swept by the workspace script
`Tools/Scripts/move-namespace.ps1` in the same release, which does exactly the three steps above.

## Unreleased

**`MMCA.Common.AI` is a new optional package: no action unless you adopt it.** Nothing in the
framework references it, so it arrives only if you add its `PackageReference` and an `Ai`
configuration section. Adopting it means one `AddMmcaChatClient(configuration)` call and a section
whose `Enabled` defaults to `false`; leaving it out changes nothing. Note that the package count in
[FACTS.md](FACTS.md) moves, so a consumer that pins every `MMCA.Common.*` entry in lockstep simply
has one more id available, not one more to add.

Two model-level changes follow. Nothing is renamed, so no `using` moves; both are felt as migrations.

1. **Relationships restrict by default.** `RestrictDeleteByDefaultConvention` sets
   `DeleteBehavior.Restrict` on every foreign key no entity configuration configured, on every
   relational engine. What moves, per relationship:

   | Before (EF's default) | After | Why |
   |---|---|---|
   | Required relationship: `Cascade` | `Restrict` | A cascade deletes children in the database, below the aggregate's invariants, below the soft-delete filter and below the domain events the framework raises |
   | Optional relationship: `ClientSetNull` | `Restrict` | A client-side null-out blanks the column of whichever children happen to be loaded and leaves the rest untouched |
   | Configured with `OnDelete(...)` | unchanged | The convention supplies a default, it never overrides a decision |
   | Ownership (owned types) | unchanged (`Cascade`) | An owned type has no identity apart from its owner; EF requires the cascade |

   The mechanical fix, per consumer:

   1. Build, then scaffold one migration per relational database (`dotnet ef migrations add
      RestrictDeletesByDefault -- --datasource <Name>`). Expect a `DropForeignKey`/`AddForeignKey`
      pair per changed relationship plus the new `MMCA:DeleteBehaviorSource` annotation in the model
      snapshot; no columns or data move.
   2. Read the generated pairs before applying them. Where the business really does want the children
      to go with the parent, opt back in **in the entity configuration**, with the reason beside it:
      `builder.HasOne(x => x.Parent).WithMany(p => p.Children).HasForeignKey(x => x.ParentId)
      .OnDelete(DeleteBehavior.Cascade); // order lines have no life without their order`, then
      re-scaffold. SQL Server's multiple-cascade-path limit applies only to the cascades you opt back
      into; restricting can never create a new path.
   3. Subclass `DeleteBehaviorConventionTestsBase` in the repo's architecture tests so the next
      accidental cascade fails a test instead of shipping.

   A host that deletes parents while children exist now gets a failed save where it used to get a
   silent child delete. That is the point, but it is a behavior change: check the delete paths the
   app actually exercises.

2. **The internal `ValReturn<T>` keyless entity is removed.** No code referenced it. The next
   migration a consumer scaffolds drops the four keyless entity entries
   (`ValReturn<bool|int|DateTime|string>`) from its model snapshot; that is a snapshot-only diff with
   no schema operations, because no table, view, column or index ever existed for them. Use
   `IRawSqlQueryExecutor` for raw scalar or DTO reads.

## 1.188.0

Security hardening release (the 2026-09-07 review). Nothing is removed or renamed; every change is
a default that moved to the secure side or a new check on caller-supplied input. The mechanical fix
per item:

1. **Fallback authorization policy.** `AddAuthorizationPolicies()` (also reached through
   `AddForwardedJwtBearer`) now sets `AuthorizationOptions.FallbackPolicy` to
   `RequireAuthenticatedUser`. Put `[AllowAnonymous]` on every controller action and
   `.AllowAnonymous()` on every minimal-API endpoint that is meant to be public; give every public
   YARP route `"AuthorizationPolicy": "anonymous"` in the gateway route config (a proxied route has
   no endpoint metadata of its own); add extra static roots to
   `FallbackAuthorizationOptions.ExemptPathPrefixes`. Add the framework's own anonymous surfaces
   (`OAuthControllerBase` challenge and completion actions, the `MMCA.Common.UI.Pages.*` credential
   and landing pages) to the repo's `AnonymousEndpointTests` allow-list when the test reports them,
   and consider `RequireExplicitAuthorizationDecision => true`. Opt-out for a host that cannot adopt
   yet: `AddAuthorizationPolicies(options => options.Enabled = false)`. An issuer-less host with no
   authentication handler gets a 500 rather than a 401 on the first gated endpoint; register a
   denying scheme or keep every endpoint declared.
2. **Query contract.** A `sortColumn`, filter key or lookup `nameProperty` that names a property
   only the entity carries returns 400. For each such key either add the field to the DTO, add a
   `DTOToEntityPropertyMap` entry, or override `EntityQueryService.FieldContract` /
   `LookupNameContract` for that endpoint. Navigation paths are capped at three segments and lookups
   at 1000 rows.
3. **Application namespace.** An unset `Application:Namespace` now derives a prefix from the
   application name for cache keys, lock keys, the SignalR backplane channel and broker endpoints.
   A cold cache is harmless; a broker rename is not. The broker formatter also changed (kebab-case
   with a prefix instead of MassTransit's default), so **no `MessageBus:EndpointPrefix` value
   reproduces the pre-upgrade queue names**: an existing deployment sets
   `MessageBus:PreserveDefaultEndpointNames=true` to keep its queues and subscriptions, and pins
   `Cache:KeyPrefix` explicitly if it wants a stable keyspace. A fresh deployment, or one that
   drains its queues at cutover, takes the new prefixed names.
4. **SMTP transport security.** `Smtp:EnableSsl` unset resolves to `true` outside Development. A
   relay that offers no TLS must set it to `false` explicitly (one startup warning is logged).
5. **Password change and reset revoke sessions** only when the app passes `IRefreshSessionStore`
   (and `TimeProvider`) to its `ChangePasswordHandler` / `ResetPasswordHandler` base call.
6. **Reset links** carry `#email=...&token=...` in the URL fragment; update mail templates and E2E
   assertions that expect the query-string form.
7. **Rate limiting and health.** `RateLimiting:HubPathPrefixes` (default `/hubs`) and
   `RateLimiting:AnonymousHubPermitLimit` (60 per minute) meter anonymous hub traffic;
   `PushNotifications:MaxConnectionsPerUser` defaults to 20; `HealthChecks:CacheSeconds` defaults to
   5 (0 disables); `GatewayRateLimiting:TrustedCallerSecret` and `TrustedCallerHeaderName` let a UI
   host's server-to-server calls escape the per-IP partition.
8. **MAUI heads** call `window.AttachMmcaAppLifecycle(services)` in `App.CreateWindow` for the
   biometric re-lock; custom `ILocalCacheStore` implementations override `ClearAsync()`.

## [1.185.0] - 2026-09-03

**Breaking: one added constructor parameter on `DeleteUserHandlerBase<TUser, TCommand>`.** No
namespace, type or configuration key moved.

The shared account-erasure workflow now writes the soft-deleted user marker itself (ADR-047), so it
needs the cache:

```diff
-DeleteUserHandlerBase(IUnitOfWork unitOfWork, ILogger logger)
+DeleteUserHandlerBase(IUnitOfWork unitOfWork, ICacheService cacheService, ILogger logger)
```

The mechanical fix, in each app's `DeleteUserHandler`:

1. Add `ICacheService cacheService` to the handler's own constructor (if it is not already there) and
   pass it to the base as the **second** argument, between `unitOfWork` and `logger`. `ICacheService`
   lives in `MMCA.Common.Application.Interfaces` and is already registered by `AddCaching()`, so no
   DI change is needed.
2. Delete any hand-rolled marker write the handler queued on the `afterCommit` collection from
   `OnAfterSoftDeleteAsync`, together with the `LoggerMessage` partial that logged its failure. The
   base writes the marker first, ahead of the whole `afterCommit` tail, and handles the failure the
   same way. A handler that kept its own copy would only re-stamp the same key under the same
   lifetime.
3. If deleting that write leaves `ICacheService` unused elsewhere in the handler, keep the
   constructor parameter anyway: the base needs it.

## [1.184.0] - 2026-09-03

**Breaking: namespace moves only.** No type, member, signature, configuration key, database object
or runtime behavior changed. Eight flat public namespaces that each held 17 to 23 types were split
by concern (feature by folder, rubric §5); the folder and the namespace stay equal.

| Old namespace | Types | New namespace(s) |
|---|---|---|
| `MMCA.Common.Application.Interfaces.Infrastructure` (dissolved) | `IRepository`, `IUnitOfWork`, `IQueryableExecutor`, `IUpdatePropertySetter`, `IUniqueConstraintViolationDetector`, `IEntityConfigurationAssemblyProvider`, `IDataSourceService`, `DataSourceKey`, `IOutboxAdministration` | `MMCA.Common.Application.Interfaces.Infrastructure.Persistence` |
| | `IPushNotificationSender`, `INativePushSender`, `IPushDeviceRegistrar`, `ILiveChannelPublisher`, `INotificationRecipientProvider`, `NullNotificationRecipientProvider` | `MMCA.Common.Application.Interfaces.Infrastructure.Notifications` |
| | `IFileStorageService`, `IImageProcessor`, `ImageContentSniffer` | `MMCA.Common.Application.Interfaces.Infrastructure.Storage` |
| | `ICurrentUserService`, `IPasswordHasher`, `ITokenService`, `ISoftDeletedUserValidator` | `MMCA.Common.Application.Interfaces.Infrastructure.Auth` |
| | `IEmailSender` | `MMCA.Common.Application.Interfaces.Infrastructure.Mail` |
| `MMCA.Common.Application.Interfaces` (keeps `ICacheService`, `ICorrelationContext`, `IDistributedLock`, `IScheduledJob`, `ITenantContext`, `IAuditTrailReader`) | `IEventBus`, `IEventUpcaster`, `IEventUpcasterRegistry`, `IIntegrationEventHandler`, `IDomainEventDispatcher`, `IDomainEventHandler` | `MMCA.Common.Application.Interfaces.Events` |
| | `IEntityDTOMapper`, `IEntityDTOProjector`, `IEntityQueryService`, `ICreateRequest` | `MMCA.Common.Application.Interfaces.Mapping` |
| | `INavigationMetadata`, `INavigationPopulator`, `NavigationMetadata` | `MMCA.Common.Application.Interfaces.Navigation` |
| `MMCA.Common.Application.UseCases` (keeps `CqrsContractInspector`) | `ICommand`, `IQuery`, `ICommandHandler`, `IQueryHandler`, `ICommandWithRequest` | `MMCA.Common.Application.UseCases.Contracts` |
| | `ICacheInvalidating`, `IFeatureGated`, `IHasTimeout`, `IQueryCacheable`, `IRequiresPermission`, `ITransactional` | `MMCA.Common.Application.UseCases.Markers` |
| | `CreateEntityHandler`, `CreateEntityHandlerBase`, `DeleteEntityCommand`, `DeleteEntityHandler`, `UpdateEntityCommand`, `UpdateEntityHandler`, `MutateEntityHandlerBase`, `ChildEntityHandlerBase`, `IEntityUpdateCommandApplier`, `MutationContext` | `MMCA.Common.Application.UseCases.Crud` |
| `MMCA.Common.Shared.Auth` (keeps `AuthClaimTypes`, `ClaimsPrincipalExtensions`, `RoleNames`, `RoleValue`) | `LoginRequest`, `RegisterRequest`, `RefreshTokenRequest`, `ForgotPasswordRequest`, `ResetPasswordRequest`, `ChangePasswordRequest`, `ChangePreferencesRequest`, `OAuthCodeExchangeRequest` | `MMCA.Common.Shared.Auth.Requests` |
| | `AuthenticationResponse`, `RefreshSessionSummaryResponse`, `UserPreferencesResponse` | `MMCA.Common.Shared.Auth.Responses` |
| | `IPermissionRegistry`, `PermissionRegistry`, `PermissionRegistryBuilder` | `MMCA.Common.Shared.Auth.Permissions` |
| `MMCA.Common.Shared.ValueObjects` (keeps `ValueObject`, `Enumeration`, `EnumerationJsonConverterFactory`) | `Address`, `AddressInvariants`, `Email`, `EmailInvariants`, `PhoneNumber`, `PhoneNumberInvariants` | `MMCA.Common.Shared.ValueObjects.Contact` |
| | `Money`, `Currency`, `CurrencyJsonConverter` | `MMCA.Common.Shared.ValueObjects.Financial` |
| | `DateRange`, `DateTimeRange` | `MMCA.Common.Shared.ValueObjects.Time` |
| `MMCA.Common.API.Startup` (keeps `WebApplicationBuilderExtensions`, `WebApplicationExtensions`, `ModuleHostContext`, `ModuleHostExtensions`, `DatabaseInitializationExtensions`, `MiniProfilerExtensions`, `SignalRExtensions`) | `MiddlewarePipelineBuilder`, `MiddlewarePipelineStep`, `MiddlewarePipelineStepNames` | `MMCA.Common.API.Startup.Pipeline` |
| | `AppAssociationEndpointExtensions`, `AppAssociationOptions`, `JwksEndpointExtensions`, `OidcDiscoveryEndpointExtensions`, `OpenApiEndpointExtensions` | `MMCA.Common.API.Startup.Endpoints` |
| | `JwtAuthorityExtensions`, `InsecureJwtMetadataWarningStartupFilter` | `MMCA.Common.API.Startup.Auth` |
| `MMCA.Common.Testing` (dissolved; `MMCA.Common.Testing.Builders` is unchanged) | `SqlServerIntegrationTestFixtureBase`, `ServiceBusEmulatorFixtureBase`, `CrossServiceFixtureBase`, `CrossServiceDataSource`, `IIntegrationTestFixture`, `IntegrationTestBase`, `ProductionHostApplicationFactory` | `MMCA.Common.Testing.Fixtures` |
| | `ProblemDetailsContractTestsBase`, `OpenApiContractTestsBase`, `ServiceInfoVersioningContractTestsBase`, `SecurityHeadersTestsBase`, `GracefulShutdownTestsBase`, `DecoratorPipelineOrderTestsBase`, `MiddlewarePipelineOrderTestsBase`, `MmcaGatewayHardeningTestsBase` | `MMCA.Common.Testing.Conformance` |
| | `JwtTokenGenerator`, `TestPolling`, `RecordingHttpForwarder`, `DependencyInjectionAssert`, `FeatureManagementTestExtensions`, `RateLimiterTestExtensions`, `HandlerTestBase` | `MMCA.Common.Testing.Support` |
| `MMCA.Common.Infrastructure.Persistence.Outbox` (keeps `OutboxMessage`) | `OutboxProcessor`, `OutboxFinalizer`, `OutboxCycleResult`, `OutboxSignal`, `IOutboxSignal`, `OutboxMetrics`, `EventNameResolver` | `MMCA.Common.Infrastructure.Persistence.Outbox.Processing` |
| | `OutboxAdministration`, `OutboxCleanupService`, `OutboxDisabledNoticeService`, `OutboxSettings` | `MMCA.Common.Infrastructure.Persistence.Outbox.Administration` |

Not moved, deliberately: `MMCA.Common.Application.UseCases.Decorators` (one concept, nine
cross-cutting concerns times command and query), `MMCA.Common.Domain.Interfaces` (the entity marker
interfaces) and `MMCA.Common.Testing.Architecture` (a flat namespace by design, ADR-015).

Extension methods are the one place the compiler cannot point you at the new `using`: a call such
as `app.MapJwksEndpoint()` now needs `using MMCA.Common.API.Startup.Endpoints;`, and the error is
CS1061 ("does not contain a definition") rather than a missing type.

## [1.183.0] - 2026-09-02

**Breaking: namespace moves only** (feature-by-folder reorganization of the framework packages,
rubric §5). No type, member or behavior changed.

| Old namespace | New namespace(s) |
|---|---|
| `MMCA.Common.Infrastructure.Services` and `MMCA.Common.Infrastructure.Settings` (dissolved) | `MMCA.Common.Infrastructure.Messaging` (+ `.Consumers`), `.Notifications.Push`, `.Notifications.Live`, `.Storage`, `.Auth`, `.Context`, `.Mail`, `.Scheduling`, `.Caching`, `.Persistence` (+ `.DataSources`, `.AuditTrail`, `.Outbox`, `.Tenancy`), each settings class beside the feature it configures |
| `MMCA.Common.Infrastructure.Hubs` (`NotificationHub`) | `MMCA.Common.Infrastructure.Notifications` |
| `MMCA.Common.UI.Services.Capabilities`, `.Capabilities.Browser`, `.Capabilities.Fallbacks` | `MMCA.Common.UI.Services.Capabilities.{Accessibility, Auth, DeviceStatus, DeviceStorage, Geo, Interop, Media, Navigation, Notifications}` (contract, browser implementation and null fallback together per family) |
| `MMCA.Common.UI.Maui.Capabilities` | `MMCA.Common.UI.Maui.Capabilities.{same families}` |
| `MMCA.Common.UI.Services` (root grab-bag) | `MMCA.Common.UI.Services.Api`, `.Culture`, `.Preferences`, `.Navigation`; `ThemeService` to `MMCA.Common.UI.Theme`; `MMCA.Common.UI.Services.Auth` gained `.Tokens` and `.OAuth` |
| `MMCA.Common.UI.Components` | `MMCA.Common.UI.Components.{PageState, Lists, Sharing, Forms, Auth}`; theme components to `MMCA.Common.UI.Theme`, culture components to `MMCA.Common.UI.Globalization` (consumer `_Imports.razor` files need the new `@using` lines) |

`MMCA.Common.Testing.Architecture` was reorganized folder-only (`Rules/{Topic}/`, `Bases/{Topic}/`);
its namespace did not change.

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

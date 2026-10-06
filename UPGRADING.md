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

## [Unreleased]

**Behavior changes from the ninth bug-hunt wave.** No public signature is removed or renamed; the
items below either need one mechanical step in a consumer or change what a consumer observes.

1. **`OwnsMoney` precision (L125).** The amount column is now configured `HasPrecision(18, 2)`. A
   consumer that maps `OwnsMoney` gets a model-snapshot change with no schema change on SQL Server
   (the column was already `decimal(18,2)`). Add one migration per affected context and confirm its
   `Up()`/`Down()` are empty, for example
   `dotnet ef migrations add OwnsMoneyPrecision -- --datasource Catalog`; the EF model-drift gate
   fails the version bump until it exists. MMCA.Store needs one for Catalog and one for Sales.
2. **Owned value objects stamp their owner (M181).** Editing only an owned value (an address, a
   money amount) now moves the owner's `LastModifiedOn/By`, re-stamps its `RowVersion` on PostgreSQL
   and SQLite, and writes trail rows named `Navigation.Property` for an `IAuditedEntity` owner. A
   client holding the owner's old concurrency token after such an edit now gets a conflict, as for
   any other update. No code change.
3. **Scoped integration event handlers see the restored origin (M184).** A handler built on
   `ScopedIntegrationEventHandlerBase` now resolves the delivery's tenant, principal and correlation
   id in its own scope: audit stamps name the original user instead of the system sentinel, and on a
   tenancy host its unit of work routes to the original tenant. A handler that relied on running as
   the system user, or that opens a scope for a different tenant inside the delivery (the seeded
   tenant cannot then be changed), must be reviewed. No code change otherwise.
4. **Typed REST clients no longer retry POST or PATCH (M185).** A POST or PATCH through
   `AddTypedServiceClient` that times out or gets a 5xx, 408 or 429 now fails on the first attempt
   instead of being replayed once. Make the endpoint idempotent (`[Idempotent]` plus an
   `Idempotency-Key`) and retry explicitly where a replay is wanted.
5. **Shaped query specifications no longer compose (M179).** `And`/`Or`/`Not` throw
   `ArgumentException` for a `QuerySpecification` that called `AddInclude`, `AddOrderBy`,
   `ApplyPaging`, `WithTracking` or `WithSoftDeleted`. Move the extra predicate into that
   specification's own `Criteria` (or compose plain `Specification` types and pass the shaped one
   alone). No first-party consumer composes one.
6. **Concurrent duplicate push sends (M180).** The losing request of two concurrent sends with the
   same dedup key now gets 409 instead of 200 with the winner's DTO; a retry gets the winner. A
   client that treats 409 as final should retry once on a dedup-keyed send.
7. **Sort column (L120).** A comma-separated `sortColumn` is now 400. Send one column.
8. **Session cookie lifetime (L126).** The cookies now follow `Jwt:RefreshTokenExpirationDays` when
   the host binds `Jwt`. A UI host that does not bind `Jwt` and runs a non-default refresh lifetime
   sets `SessionCookieSettings.Lifetime` (for example in the same `Configure<SessionCookieSettings>`
   call the same-origin proxy uses).
9. **Distributed rate-limit keys (L127).** Only for `RateLimiting:Distributed=true`: the Redis key
   format becomes `rl:{namespace}:{scope}:{partition}:{window}`. The first deploy abandons the
   in-flight counters, so every partition gets one fresh one-minute allowance; the old keys expire on
   their own TTL. Nothing to migrate.
10. **Zero `Money` on the wire (L144).** `{"amount":0,"currency":""}` now deserializes to
    `Money.Zero()` instead of throwing; a client that relied on the 400 for an empty currency code
    no longer gets it.
11. **Token storage constructors (L132).** `WasmTokenStorageService` and `ServerTokenStorageService`
    gain an optional trailing `TimeProvider? timeProvider = null`. Source-compatible; a host that
    registers them through the framework extension methods changes nothing, and code compiled
    against the old constructor recompiles unchanged.
12. **User-admin search placeholder (L154).** The placeholder text is now "Search by exact email
    address..." (and the matching Spanish text). An E2E page object that locates the box by its
    placeholder (MMCA.ADC `PageObjects/Identity/UserListPage.cs`) must use the new text.
13. **Proxied traffic and the UI edge limiter (M190).** A proxied request whose last segment has a
    file extension (`/api/report.csv`) now counts against the per-IP window and the concurrency
    ceiling. Proxied hub traffic stays exempt at whatever `SameOriginApiProxy:PathPrefix` the host
    configures.

## [1.231.0] - 2026-10-06

**`ConstructorDependencyCountTestsBase` also fails when a ceiling is loose, `FormsConventionTestsBase`
changes its `RequiredMarkers` default and gains two facts, and an AppHost that keeps a local
`WithSelectedBroker` extension no longer compiles.** No runtime API changes; every item is a test base
or an AppHost helper, and each fix is mechanical.

Old-to-new map:

| Old | New |
|-----|-----|
| `ConstructorDependencyCountTestsBase` fails only above a ceiling | also fails when the widest class of a population sits below its ceiling (`*_ConstructorDependencyCeilingIsTight`) |
| `FormsConventionTestsBase.RequiredMarkers` default: guard markers + `Required="true"` + `RequiredError` | guard markers + `Model="_model"`, `Validation="@_validate"`, `<ErrorSummary`, `Result="_saveResult"`, `Messages="_form?.Errors"`, `Validation.CorrectFollowing` |
| subclass-authored `AdminCreateForms_ReadRequirednessOffTheirModel` and `ProfileForm_KeepsErrorSummaryAndPasswordValidation` | inherited from `FormsConventionTestsBase` (same names; a local copy now hides the base member, CS0108) |
| app-local `BrokerSelection.WithSelectedBroker(attach)` in the AppHost | `MMCA.Common.Aspire.Hosting.BrokerSelection.WithSelectedBroker(attach)` (both imported is an ambiguous call, CS0121) |
| app-local `JwtAudience` (MMCA.ADC: `MMCA.ADC.Identity.Shared.Authorization.JwtAudience`) | `MMCA.Common.API.Startup.Auth.JwtAudience`, same members (a host importing both namespaces gets CS0104) |

The fix:

1. **Constructor-dependency ceilings.** Run the subclass's `*ConstructorDependencyCountTests*`; each
   `*_ConstructorDependencyCeilingIsTight` failure names the ceiling property, its value and the
   observed maximum. Lower the ceiling to that number (MMCA.ADC's service ceiling 10 to 8 and
   MMCA.Store's 9 to 6 at the time of this release; re-check the controller and handler marks the
   same way). Never raise a ceiling to make the fact pass.
2. **Forms convention.** Delete the subclass's `RequiredMarkers` override when it equals the new
   default, and delete its local `AdminCreateForms_ReadRequirednessOffTheirModel`,
   `ProfileForm_KeepsErrorSummaryAndPasswordValidation` and `CountOccurrences`, leaving `Map` and
   `MinimumCreateForms`. A Profile page outside
   `Source/Modules/Identity/{RepoToken}.Identity.UI/Pages/Users/Profile/Profile.razor` overrides
   `ProfileFormPath`. A subclass that relied on the old `Required="true"` default overrides
   `RequiredMarkers` with it explicitly.
3. **Broker selection.** Delete the AppHost's local `BrokerSelection.cs`. Optionally replace the
   hand-written `withBroker` switch with
   `var withBroker = builder.AddSelectedBroker("MYAPP_BROKER", sqlServer);`; the
   `.WithSelectedBroker(withBroker)` calls stay as they are.
4. **JWT audience.** Delete the app-local `JwtAudience` class and its `using`; the hosts already import
   `MMCA.Common.API.Startup.Auth` for `GetRequiredJwtAuthority`, so
   `JwtAudience.RequireConfigured(builder.Configuration[JwtAudience.ConfigKey])` compiles unchanged.
   Keep the old `using` where the file still needs other types from that namespace.

The new bases (`CostTagConventionTestsBase`, `MessageBusBackpressureTestsBase`,
`ForwardedJwtAudienceTestsBase`, `InlineStyleTestsBase`, `LifetimeTokenConventionTestsBase`) change
nothing until a repo subclasses them. `LifetimeTokenConventionTestsBase` fails on every raw
`_cts.Token` read, so migrate those reads to `_cts.LifetimeToken()` (`MMCA.Common.UI.Common`) before
adopting it.

## [1.218.0] - 2026-10-01

**Constructor changes on `AuthenticationServiceBase<TUser>` and six infrastructure services, a
renamed unit-of-work member, a conditional `IRawSqlQueryExecutor` registration, a SQLite
index-filter change, a fail-closed CSV export, `RefreshSession` setters, the bundled Bootstrap CSS
removed, two `[EditorRequired]` parameters and two new `ICookieSessionRefresher` members.** A host that
resolves the framework services from DI changes nothing for the six infrastructure constructors;
every other item is listed with its own fix. The same-origin API proxy is new and opt-in; its wiring
closes this section.

Old-to-new map:

| Old | New |
|-----|-----|
| `AuthenticationServiceBase<TUser>(unitOfWork, tokenService, passwordHasher, loginProtection, timeProvider, validators, refreshSessions, refreshSessionSettings, twoFactor = null, emailConfirmationSettings = null)` | `AuthenticationServiceBase<TUser>(unitOfWork, passwordHasher, loginProtection, validators, IAuthSessionIssuer sessionIssuer, twoFactor = null, emailConfirmationSettings = null)` |
| protected `TimeProvider`, `RefreshSessions`, `AccessTokenLifetime`, `RefreshTokenLifetime`, `MaxActiveSessionsPerUser` | removed (internal to `AuthSessionIssuer`); `TokenService`, `UnitOfWork`, `Repository` stay |
| `IUnitOfWork.RequestIdentityInsert()` (also `IDbContextFactory`, `DbContextFactory`) | `RequestExplicitKeyInsert()` |
| `IRawSqlQueryExecutor` always registered; throws `NotSupportedException` on a Cosmos default | registered only when the default data source is relational |
| SQLite filtered-index predicates on `OutboxMessages` (`ProcessedOn`, `OrderingKey`), `InternalCommands` (`ProcessedOn`, `DeadLetteredOn`) and push notifications (`DedupKey`) quote `[Column]` | quote `"Column"`, for example `"DedupKey" IS NOT NULL` |
| `OutboxProcessor`, `InternalCommandProcessor`, `OutboxCleanupService`, `InternalCommandCleanupService`, `OutboxAdministration`, `InternalCommandAdministration` take `IEntityDataSourceRegistry`, `IDataSourceResolver`, `IOptions<TenancySettings>? = null` | take one `FrameworkTableTargets tableTargets` in their place |
| `ExportAsync` with a null read specification streams the whole table | 403 `Export.RowScopeRequired` unless `AllowUnscopedExport` is overridden to `true` |
| `RefreshSession.IpAddress { get; init; }`, `UserAgent { get; init; }` | `{ get; private set; }` |
| `_content/MMCA.Common.UI/lib/bootstrap/dist/css/bootstrap.min.css` | removed |
| `.navbar-toggler` | `.nav-toggler` |
| `PageHeader.Title`, `MobileCardList.Items` optional | `[EditorRequired]` |
| `ICookieSessionRefresher { GetOrRefreshAsync }` | `+ Task<SessionRefreshOutcome> ValidateOrRefreshAsync(HttpContext context, CancellationToken cancellationToken = default)`, `+ Task<SessionRefreshOutcome> RefreshAsync(HttpContext context, CancellationToken cancellationToken = default)` |

The mechanical fix:

1. **`AuthenticationServiceBase<TUser>` subclasses.** Change the primary constructor to take
   `IAuthSessionIssuer sessionIssuer` (`using MMCA.Common.Application.Auth.Sessions;`) in place of
   `ITokenService`, `TimeProvider`, `IRefreshSessionStore` and `IOptions<RefreshSessionSettings>`, and
   pass `(unitOfWork, passwordHasher, loginProtection, validators, sessionIssuer, twoFactor,
   emailConfirmationSettings)` to the base. `AddInfrastructure` registers `IAuthSessionIssuer`
   scoped, so a DI-resolved subclass needs nothing else. A subclass that read the removed members
   injects what it needs itself (`TimeProvider`, `IRefreshSessionStore`) or calls the issuer
   (`ListActiveAsync`, `RevokeSessionAsync`, `SignOutAsync`, `SignOutEverywhereAsync`). An override of
   the token lifetimes or the session cap becomes configuration: `Jwt:AccessTokenExpirationMinutes`,
   `Jwt:RefreshTokenExpirationDays`, `RefreshSessions:MaxActiveSessionsPerUser`. A hand-built
   subclass (tests) constructs `new AuthSessionIssuer(tokenService, refreshSessions,
   refreshSessionSettings, timeProvider)` and passes it.
2. **`RequestIdentityInsert()`.** Replace every call with `RequestExplicitKeyInsert()`; behavior is
   unchanged (SET IDENTITY_INSERT on SQL Server, a no-op on the engines that need none).
3. **`IRawSqlQueryExecutor` on a Cosmos default.** A host whose default source is Cosmos and that
   injects `IRawSqlQueryExecutor` now fails at container validation. Remove the dependency, or
   resolve it optionally (`serviceProvider.GetService<IRawSqlQueryExecutor>()`) where a relational
   source may be absent. Relational hosts change nothing.
4. **SQLite consumers with migrations.** The filtered outbox, internal-command and
   push-notification `DedupKey` index predicates now quote with double quotes. Add a migration per
   SQLite source (`dotnet ef migrations add QuoteSqliteIndexFilters -- --datasource <Name>`) and
   apply it, or regenerate the model snapshot where the source is not migrated. The index columns
   and semantics are unchanged.
   SQL Server, PostgreSQL and Cosmos sources change nothing.
5. **Hand-built outbox and internal-command services (tests, custom composition).** Replace the
   three arguments `entityDataSourceRegistry, dataSourceResolver, tenancyOptions` with
   `new FrameworkTableTargets(entityDataSourceRegistry, dataSourceResolver, tenancyOptions)`
   (`using MMCA.Common.Infrastructure.Persistence.DataSources;`), in the parameter position the map
   above shows. `AddInfrastructure` registers it singleton.
6. **CSV export (fail-closed).** A controller whose `GetReadSpecificationAsync` returns null and that
   intends a whole-table export adds `protected override bool AllowUnscopedExport => true;` (read per
   request, so it may depend on the principal). Every other controller either returns a scoping
   specification or accepts the 403 `Export.RowScopeRequired`; a client calling the export should
   handle that 403.
7. **`RefreshSession.IpAddress` / `UserAgent`.** Code that set them in an object initializer passes
   them to `RefreshSession.Create(...)` instead; clearing them is `Anonymize()`.
8. **Bootstrap CSS.** Delete the `<link>` to `_content/MMCA.Common.UI/lib/bootstrap/dist/css/bootstrap.min.css`
   from the Blazor Web `App.razor` and from the MAUI and WebAssembly `index.html`. Rename any CSS
   override, script or E2E selector targeting `.navbar-toggler` to `.nav-toggler`. A consumer page
   that still uses Bootstrap utility or component classes either restyles with MudBlazor or ships
   its own Bootstrap copy.
9. **`[EditorRequired]` parameters.** Pass `Title` on every `<PageHeader>` and `Items` on every
   `<MobileCardList>`; an omission is RZ2012, an error under `TreatWarningsAsErrors`. Every
   first-party usage already passes both.
10. **`ICookieSessionRefresher` implementations** (test fakes): add `ValidateOrRefreshAsync` (the
    validate-or-refresh step) and `RefreshAsync` (a forced refresh that ignores the current access
    token's expiry). Both return a `SessionRefreshOutcome`: `SessionRefreshOutcome.Refreshed(token)`,
    `Rejected()` when there is no refresh cookie or the identity endpoint refused it, and
    `Unavailable(retryAfter)` when the refresh could not be decided right now.
11. **Password rule (behavior change, no code change).** The server and the client form evaluate the
    same Unicode-aware `PasswordComplexity`: a new password whose only special character is a
    non-ASCII letter is now rejected. Update any help text or seeded test password that relied on
    it. Existing hashes are unaffected.

**Opting in to the same-origin API proxy (optional, Blazor Web hosts).** In the UI host's
`Program.cs`:

1. Register `AddCommonSameOriginApiProxy(builder.Configuration)` (`using
   MMCA.Common.UI.Web.SameOriginProxy;`) AFTER `AddServerAuthSessionCookie(...)`,
   `AddClientAuthSessionCookieSync()`, `AddCommonServerTokenStorage()` and any host
   `ITokenRefresher` registration: it replaces the Server circuit's `ITokenRefresher` and
   `ISessionCookieSync`, and `MapCommonSameOriginApiProxy()` fails the boot naming the registration
   that displaced them.
2. Map `app.MapCommonSameOriginApiProxy();` next to `app.MapSessionCookieEndpoints();`, after
   `UseAuthorization`.
3. Configure the `SameOriginApiProxy` section only where a default does not fit: `PathPrefix`
   (`/api`), `GatewayAddress` (defaults to `Api:ApiEndpoint`, service-discovery names included),
   `SessionCookieSameSite` (`Strict`), `AdditionalTokenIssuingPaths` (for example `auth/2fa/verify`),
   `RefreshPath` (`auth/refresh`), `RevokePath` (`auth/revoke`).
4. Expect the session cookies to become `SameSite=Strict` and claims-only toward script:
   `/auth/session/token` returns an unsigned claims copy, `POST /auth/session-cookie` ignores
   script-supplied tokens, and `/client-config` adds `api.sameOriginApiEndpoint`, which switches the
   WebAssembly `APIClient`, the notification hub and `ApiFileDownloadButton` to the proxy. Client
   code that sends its own requests to the proxy adds `X-CSRF: 1` on every unsafe method
   (`SameOriginProxyHeaders`); the `APIClient` does it already. The proxy refuses (403) any request
   whose `Origin` or `Sec-Fetch-Site` names another origin, including a sibling subdomain, and every
   WebSocket upgrade without the host's own `Origin`; it compares against the request's scheme, host
   and port as the app sees them, so a host behind a TLS-terminating proxy or ingress must call
   `app.UseCommonUiForwardedHeaders()` first, or every browser POST is refused. `OPTIONS` is answered
   locally and never reaches the gateway. Client code that decoded the
   browser-held access token keeps working for claims but can no longer present it to the gateway.
5. Optionally gate the WebAssembly download size in CI with the composite action
   `uses: ivanball/MMCA.Common/.github/actions/wasm-payload-budget@main` (inputs `project`, `budget-kb`,
   optional `configuration`, `output-dir`).

A host that does not call `AddCommonSameOriginApiProxy` is unchanged.

## [1.217.0] - 2026-10-01

**`AddCommonOpenApi()` no longer registers an OpenAPI document; the host registers it with its own
`services.AddOpenApi()`.** The host call has to live in the host project: the OpenAPI XML-comment
source generator attaches the host's controller summaries by intercepting the `AddOpenApi` call
sites of the project being compiled. With no strongly typed identifiers the document is exactly the
one plain `AddOpenApi()` produces.

Old-to-new map:

| Old | New |
|-----|-----|
| `AddCommonOpenApi()` registers one document per API version (`AddApiVersioning().AddOpenApi()`) | registers no document; configures every document the host registers (strongly-typed-identifier transformers, backfill guard) |
| Host calls `AddCommonOpenApi()` alone | Host calls `services.AddOpenApi();` then `services.AddCommonOpenApi();` |
| `MapCommonOpenApi()` maps `MapOpenApi().WithDocumentPerVersion().AllowAnonymous()` | maps `MapOpenApi().AllowAnonymous()`; outside Production throws `InvalidOperationException` when no `v1` document is registered |
| `info.title` `<entry assembly> \| v1`, `info.version` `1.0`, `enum: ["1.0"]` on `api-version`, `deprecated` from API-version deprecation, `<host>_v2.json` per extra version | the plain `AddOpenApi()` values: `<application name> \| v1`, `1.0.0`, no `enum`, no versioning-derived `deprecated`, one `v1` document |

The mechanical fix:

1. **Hosts calling `AddCommonOpenApi()`.** Add `services.AddOpenApi();` in the host's own
   `Program.cs` (either order relative to `AddCommonOpenApi()`). Without it, `MapCommonOpenApi()`
   throws at startup outside Production, naming the missing call.
2. **Hosts with a hand-written registration** (`services.AddOpenApi();` plus
   `app.MapOpenApi().AllowAnonymous()` outside Production) may adopt the framework pair: keep
   `services.AddOpenApi();`, add `services.AddCommonOpenApi();`, and replace the hand-written mapping
   with `app.MapCommonOpenApi();` (`using MMCA.Common.API.Startup.Endpoints;`). The committed
   build-time documents do not change.
3. **A host that wants the document behind a token** outside Production maps it itself:
   `app.MapOpenApi().RequireAuthorization();`.
4. **A host that relied on per-version documents** (`/openapi/v2.json`) registers each one itself
   with `services.AddOpenApi("v2")` (no first-party host uses more than v1.0).

## [1.216.0] - 2026-09-30

**Required constructor arguments on four DI-resolved types, one security-relevant default, the
contract-snapshot format and an analyzer severity.** A host that resolves the framework types from DI changes nothing
for the first item; the other items are listed with their own fix.

Old-to-new map:

| Old | New |
|-----|-----|
| `TokenService(jwtOptions, permissionRegistry, timeProvider, jwksSettings = null)` | `TokenService(jwtOptions, permissionRegistry, timeProvider, ILogger<TokenService> logger, jwksSettings = null)` |
| `PasswordResetTokenService(cacheService, settings, distributedLock)` | `PasswordResetTokenService(cacheService, settings, distributedLock, TimeProvider timeProvider)` |
| `EmailConfirmationTokenService(cacheService, settings)` | `EmailConfirmationTokenService(cacheService, settings, TimeProvider timeProvider)` |
| `EfInboxStore(dbContextFactory, dataSourceResolver, outboxOptions, logger)` | `EfInboxStore(dbContextFactory, dataSourceResolver, outboxOptions, logger, TimeProvider timeProvider)` |
| `CookieSessionRefresher` (internal) | takes a `TimeProvider` (no consumer impact) |
| `MapCommonOpenApi()` outside Production: `/openapi/*` under the fallback authorization policy | mapped `AllowAnonymous()` outside Production; Production still maps nothing |
| Contract line `Full.Type.Name { Member:String, ... }` | `EventNameValue { Member:String?, ... }` (keyed on `[EventName]` when declared; `?` on a nullable reference annotation, including generic arguments) |
| RS0026/RS0027 off in this repository | RS0026/RS0027 at error in this repository (contributors only) |

The mechanical fix:

1. **Hand-built auth and inbox services (tests, custom composition).** Pass the new argument:
   `NullLogger<TokenService>.Instance` or a mock logger for `TokenService`, and `TimeProvider.System`
   (or a `FakeTimeProvider` in a test) for the other three. DI-resolved hosts change nothing:
   `AddInfrastructure` registers `TimeProvider` and logging.
2. **`MapCommonOpenApi()` (security-relevant default).** Nothing to do if your host already marked
   the document anonymous (every first-party host did). A host that wants the document behind a
   token in a non-Production environment stops calling `MapCommonOpenApi()` there and maps it
   itself: `app.MapOpenApi().WithDocumentPerVersion().RequireAuthorization();`.
3. **Committed integration-event contracts (`IntegrationEventContractTestsBase` subclasses).** The
   contract fact fails until the snapshot is regenerated. Set `MMCA_CONTRACT_SNAPSHOT_OUT` to a file
   path and run the contract test once, for example
   `MMCA_CONTRACT_SNAPSHOT_OUT=contract.txt dotnet test --project Tests/Architecture/<Repo>.Architecture.Tests -- --filter-class "*IntegrationEventContractTests"`
   (or run the test project's `.exe` with `-class "*IntegrationEventContractTests"` where
   `dotnet test` discovers nothing). The run still fails, and the file holds one ready-to-paste
   literal per event; replace the body of `ExpectedContract` with it, keep any explanatory comments,
   re-run, and review the diff: a `?` that appears is a member that may now be null on the wire.
4. **RS0026/RS0027 (contributors to MMCA.Common only).** A new public overload pair with optional
   parameters fails the build. Give the new overload no optional parameters, or put the new
   parameter on a new member name; an overload already released keeps its targeted
   `SuppressMessage` at the declaration.

## [1.215.0] - 2026-09-30

**Behavior change: a transactional unit no longer commits while changes are left unsaved.** At the
end of an `ITransactional` command or an explicit `IUnitOfWork.ExecuteInTransactionAsync`, the
pipeline now inspects what is still tracked before committing:

- An internal-command row scheduled after the handler's last save is saved there, inside the
  transaction, so it commits with the aggregate change. Before, it was silently discarded.
- Any OTHER tracked change still unsaved throws `InvalidOperationException` and rolls the whole unit
  back. Before, the commit silently discarded it while the command reported success.

Mechanical fix for a handler that now throws: save before returning (`await
unitOfWork.SaveChangesAsync(ct)` after the last mutation). A save you added only to flush a
scheduled internal command (the workaround for the dropped row) is no longer needed and can be
removed.

## [1.213.0] - 2026-09-30

**Bug-hunt 2026-09-30 remediation: new required constructor parameters, three interface members and
test-surface tightenings.** Hosts that resolve these types from DI change nothing; code that
constructs them by hand, implements the interfaces, or subclasses the test bases does.

Old-to-new map:

| Old | New |
|-----|-----|
| `ChangePasswordHandlerBase(unitOfWork, passwordHasher, logger, refreshSessions, timeProvider)` | `ChangePasswordHandlerBase(unitOfWork, passwordHasher, logger, refreshSessions, ILoginProtectionService loginProtection, timeProvider)` (M108) |
| `ITwoFactorService` (six members) | adds `bool VerifyCode(string secret, string? code, out long matchedStep)` (L56) |
| `PasswordResetTokenService(cacheService, settings)` | `PasswordResetTokenService(cacheService, settings, IDistributedLock distributedLock)` (L91) |
| `ICacheService` (seven members) | adds `Task<(bool Found, T? Value)> TryGetAsync<T>(string key, CancellationToken)` with a default body (L47) |
| `MarkAllNotificationsReadHandler(unitOfWork, queryableExecutor, timeProvider)` | `MarkAllNotificationsReadHandler(unitOfWork, timeProvider)` (L49) |
| `ISessionCookieSync.SyncAsync(string, string)` / `ClearAsync()` return `Task` | return `Task<bool>` (M129) |
| `IExternalLinkService.OpenAsync(Uri, CancellationToken)` returns `Task` | returns `Task<bool>` (L70) |
| `IAuthUIService` | adds `RevokeAllSessionsAsync(CancellationToken)` (M131) |
| `CapturingHttpMessageHandler.Requests` (live list) | a snapshot taken at the time of the call (L84) |

The mechanical fix:

1. **`ChangePasswordHandlerBase` subclasses.** Add an `ILoginProtectionService loginProtection`
   constructor parameter and pass it to the base before `timeProvider` (it is registered by
   `AddInfrastructure`). A test that builds the subclass passes a mock whose `CheckLockoutAsync`
   returns `Result.Success()`.
2. **`ITwoFactorService` implementations and fakes.** Implement the new overload; a fake that has
   no time steps sets `matchedStep = 0` and delegates to the two-argument member. The shipped
   `TotpTwoFactorService` needs nothing.
3. **`PasswordResetTokenService` constructed by hand.** Pass an `IDistributedLock` (registered by
   `AddCaching`). Hosts resolving `IPasswordResetTokenService` from DI change nothing.
4. **`ICacheService` implementations.** Nothing is required: the default body infers presence
   from a non-null `GetAsync`. A store that can cache a value-type `default(T)` should override
   `TryGetAsync` with a real presence check, or `GetOrCreateAsync` re-runs the factory for it.
5. **`MarkAllNotificationsReadHandler` constructed by hand.** Drop the `IQueryableExecutor`
   argument. A test that verified `SaveChangesAsync` or inspected mutated rows asserts on the
   predicate and assignments passed to `IRepository.ExecuteUpdateAsync` instead.
6. **`ISessionCookieSync` and `IExternalLinkService` implementations.** Return `true` when the
   cookie was written or cleared (M129), or when the URL was opened (L70), `false` otherwise.
   Callers that only awaited the call keep compiling.
7. **`IAuthUIService` implementations** (none known in consumers). Add `RevokeAllSessionsAsync`
   (M131).
8. **Integration-event contract snapshots.** Regenerate the `ExpectedContract` lines whose members
   are constructed generics or come from an intermediate base (ADC `AttendeeCheckedIn`:
   `SessionId:Nullable<Int32>, SponsorId:Nullable<Int32>`; Store `OrderFulfilled`:
   `Lines:IReadOnlyList<FulfilledLine>`). Run the consumer's `IntegrationEventContractTests` and
   paste the reported live line (M133).
9. **bUnit tests.** `BunitComponentTestBase` no longer authorizes every authenticated principal. A
   test that renders `Roles="..."` content uses a principal in that role (`TestPrincipal.InRole`),
   and an `AuthorizeView Policy="X"` needs the policy registered
   (`Services.AddAuthorizationCore(o => o.AddPolicy("X", ...))`) or it throws naming the policy (L81).
10. **`CapturingHttpMessageHandler.Requests`.** Re-read the property after sending instead of
   holding the list (L84).
11. **`Ai:Timeout`.** At most `01:00:00`, written as a TimeSpan (`00:00:30`), since a bare number
   binds as days (L90).
12. **AI tools.** A tool whose `mmca.tool.consequential` value is present but not a definite false
   is now withheld until the request confirms it (M135).
13. **Profile E2E subclasses.** When the app's snackbar texts differ from "Name updated
   successfully." / "Address updated successfully." / "Email updated successfully.", override
   `NameSavedMessage` / `AddressSavedMessage` / `EmailSavedMessage` (M132).

## [1.210.0] - 2026-09-23

**Removals, tightenings and one namespace move in shipped signatures.** Hosts that resolve these types from DI
change nothing; code that constructs them by hand, mocks the removed members or reads the renamed
properties does. Configuration keys are unchanged.

Old-to-new map:

| Old | New |
|-----|-----|
| `IUnitOfWork.Save()` | removed; `await unitOfWork.SaveChangesAsync(cancellationToken)` |
| `IUnitOfWork.BeginTransaction()` / `CommitTransaction()` / `RollbackTransaction()` | removed; `await unitOfWork.ExecuteInTransactionAsync(ct => ..., cancellationToken)` (`IDbContextFactory` keeps its synchronous members) |
| `ChangePasswordHandlerBase(..., IRefreshSessionStore? refreshSessions = null, TimeProvider? timeProvider = null)` | `ChangePasswordHandlerBase(..., IRefreshSessionStore refreshSessions, TimeProvider? timeProvider = null)` |
| `ResetPasswordHandlerBase(..., IRefreshSessionStore? refreshSessions = null, TimeProvider? timeProvider = null)` | `ResetPasswordHandlerBase(..., IRefreshSessionStore refreshSessions, TimeProvider? timeProvider = null)` |
| `TimeProvider? timeProvider = null` on `TokenService`, `InProcessEventBus`, `DomainEventSaveChangesInterceptor`, `OutboxProcessor`, `OutboxCleanupService`, `InternalCommandProcessor`, `InternalCommandCleanupService`, `InternalCommandAdministration`, `RefreshSessionCleanupService` | `TimeProvider timeProvider`, required, same position |
| `IFileStorageService.UploadAsync(..., FileUploadOptions options, ...)` default interface member | abstract; every implementation provides it |
| `PhysicalDataSource(Key, ConnectionString, SqlServerMigrationsAssembly, CosmosDatabaseName)` plus `SqliteMigrationsAssembly` / `PostgreSQLMigrationsAssembly` init properties | `PhysicalDataSource(Key, ConnectionString, MigrationsAssembly, CosmosDatabaseName)`, one slot for the source's own engine |
| `MMCA.Common.Infrastructure.Scheduling.PeriodicBackgroundService` | `MMCA.Common.Infrastructure.Hosting.Background.PeriodicBackgroundService` (namespace move only; the type is unchanged) |

The mechanical fix:

1. **`IUnitOfWork` sync members.** Replace a call with the async member in the table. In a test,
   delete a `Setup` or `Verify(..., Times.Never)` on one of the four removed members: the property
   it asserted now holds by construction, because the member no longer exists.
2. **Password handler subclasses.** Pass the injected `IRefreshSessionStore` through to the base (an
   app with refresh sessions already does). A test that built the subclass without one passes an
   `IRefreshSessionStore` fake (`InMemoryRefreshSessionStore` from `MMCA.Common.Testing`) or a mock.
3. **Required `TimeProvider`.** Where a type in the list is constructed by hand (usually a test),
   add `timeProvider: TimeProvider.System`, or a `FakeTimeProvider` when the test drives time. A
   call that passed `timeProvider: null` passes `TimeProvider.System`.
4. **`IFileStorageService` implementations.** Implement the options overload and honor the
   headers; a store that has none to set forwards to the three-argument overload.
5. **`PhysicalDataSource`.** Positional construction compiles unchanged. Rename a named argument
   `SqlServerMigrationsAssembly:` to `MigrationsAssembly:`; move an initializer
   `{ SqliteMigrationsAssembly = x }` or `{ PostgreSQLMigrationsAssembly = x }` into the third
   positional argument; read `.MigrationsAssembly` where the code read any of the three old
   properties.
6. **`PeriodicBackgroundService` namespace.** In a file that derives from it, add
   `using MMCA.Common.Infrastructure.Hosting.Background;`, and remove
   `using MMCA.Common.Infrastructure.Scheduling;` when the file uses nothing else from it (the build
   reports it as IDE0005). Re-qualify any fully qualified
   `MMCA.Common.Infrastructure.Scheduling.PeriodicBackgroundService` reference. This is the
   namespace-move fix at the top of this file, applied to one type.

## [1.207.0] - 2026-09-21

**`MMCA.Common.AI` names no vendor: the provider is an adapter package selected by name.** The
`Anthropic` package reference leaves the governed package, `AiProvider` (the enum) is gone, and
`Ai:Provider` is a string matched case-insensitively against the `IAiProviderFactory` instances the
host registered. Two adapters ship: `MMCA.Common.AI.Anthropic` (`AddAnthropicAiProvider()`, name
`Anthropic`) and `MMCA.Common.AI.OpenAI` (`AddOpenAiProvider()`, name `OpenAI`). A host that names
a provider it never registered fails at startup with the registered names in the message.

The mechanical fix for a host on the Anthropic provider:

1. Reference `MMCA.Common.AI.Anthropic` beside `MMCA.Common.AI` (the host project that calls
   `AddMmcaChatClient`; the adapter is an Infrastructure-tier reference like the governed package).
2. Call `builder.Services.AddAnthropicAiProvider();` before `AddMmcaChatClient(configuration)`.
3. Keep `"Provider": "Anthropic"` in the `Ai` section (it now binds as a string; `Ai:Provider` is
   required whenever `Ai:Enabled` is true). Optional new key `Ai:Endpoint` (absolute URI) routes the
   adapter through a gateway or compatible endpoint.
4. Delete any code that constructed `AnthropicClient` for the governed pipeline itself; a test tier
   that built one directly builds its client through `AddMmcaChatClient` with the same section, or
   through the factory overload.

Old-to-new map for the public surface:

| Old | New |
|-----|-----|
| `MMCA.Common.AI.AiProvider` (enum) | removed; `AiSettings.Provider` is `string?` |
| `UsageRecordingChatClient(inner, meter, AiProvider)` | `UsageRecordingChatClient(inner, meter, string? configuredProvider)`; the `provider` tag now comes from the inner client's `ChatClientMetadata.ProviderName` (`anthropic`, `openai`, lower-case) and the configured name is only the fallback |
| `AiUsageMeter.Record(..., AiProvider)` / `RecordDuration(..., AiProvider, outcome)` | same members with `string? provider` |
| provider constructed inside `AddMmcaChatClient` | `IAiProviderFactory` (`MMCA.Common.AI.Providers`), one per adapter package |

**Dashboards and alerts:** the `provider` dimension on `mmca.ai.input_tokens`, `mmca.ai.output_tokens`
and `mmca.ai.call.duration` changes value from `Anthropic` to `anthropic`. A query filtering on the
old casing must be updated in the same release.

**Three new bounds are on by default and can refuse a host at startup or a request at the call:**

- `Ai:RequireGuardrail` (default `true`): `AddMmcaChatClient` throws at registration when
  `Ai:Enabled` is true and no `IChatGuardrail` or `IChatRequestRedactor` is registered. Register
  one (`AddPiiRedactionGuardrail()` is the framework's own) or set the key to `false` deliberately.
- `Ai:AllowTools` now needs an `IChatToolPolicy`: with `AllowTools` true and no policy registered,
  registration throws; with policies, a tool is offered only when every policy allows it, and a
  tool marked `mmca.tool.consequential` also needs its name in the request's
  `mmca.tool.confirmed` property.
- The model is pinned: a request whose `ChatOptions.ModelId` differs from `Ai:Model` is refused
  before the provider is called. A `PromptContract` whose `Model` is not the configured model now
  fails at the call rather than silently routing (Anthropic) or silently hashing (OpenAI).

**`IChatGuardrail` gained a default member** (`InspectStreamedUpdateAsync`); existing
implementations compile unchanged and inspect nothing on the streamed path until they override it.

## [1.206.0] - 2026-09-20

**`ConstructorDependencyCountTestsBase` subclasses must declare two more ceilings.** The base now
carries `MaxControllerConstructorDependencies` and `MaxHandlerConstructorDependencies` as abstract
properties beside the existing `MaxConstructorDependencies`, with a fact for each. A subclass that
does not override them fails to compile.

The mechanical fix: add both properties, each set to the repo's current high-water mark so the
adopting build is green, then lower them in the same PR as the reduction they gate. Find the marks
by setting each to `0`, running the architecture test project, and reading the offender list in the
failure message (`FullName (N ctor dependencies)`).

```csharp
public sealed class ConstructorDependencyCountTests : ConstructorDependencyCountTestsBase
{
    protected override IArchitectureMap Map { get; } = new StoreArchitectureMap();

    protected override int MaxConstructorDependencies => 8;

    protected override int MaxControllerConstructorDependencies => 8;

    protected override int MaxHandlerConstructorDependencies => 8;
}
```

The controller fact scans `Map.Api()`, which includes the framework API assembly, so a framework
controller above the ceiling is reported alongside the repo's own.

## [1.201.0] - 2026-09-13

**The framework stops owning application role vocabulary.** `MMCA.Common` named five roles and used
three of them in code. It names none now: a role is the app's word, and the framework talks about
permissions instead. Four things move.

### 1. `RoleNames` is removed: declare your own

`MMCA.Common.Shared.Auth.RoleNames` is gone. Add the same constants to your own Identity Shared
project and swap the `using`.

| Removed | Replacement |
| --- | --- |
| `MMCA.Common.Shared.Auth.RoleNames` | `<YourApp>.Identity.Shared.Auth.RoleNames` (yours to declare) |
| `RoleNames.Organizer` = `"Organizer"` | your own constant, same value |
| `RoleNames.Attendee` = `"Attendee"` | your own constant, same value |
| `RoleNames.ContentEditor` = `"ContentEditor"` | your own constant, same value |
| `RoleNames.Admin` = `"Admin"` | your own constant, same value |
| `RoleNames.Customer` = `"Customer"` | your own constant, same value |

The values do not change, so no token, database row, or claim is affected. Declare the class with
only the roles your app actually has:

```csharp
namespace YourApp.Identity.Shared.Auth;

public static class RoleNames
{
    public const string Admin = "Admin";
    public const string Customer = "Customer";
}
```

Then, from a POSIX shell at the consumer's root:

```sh
grep -rl --include='*.cs' --include='*.razor' 'MMCA.Common.Shared.Auth' . \
  | xargs grep -l 'RoleNames' \
  | xargs sed -i 's/^\(\s*\)\(global \)\?@\?using MMCA.Common.Shared.Auth;/\1\2using MMCA.Common.Shared.Auth;\n\1\2using YourApp.Identity.Shared.Auth;/'
```

Rebuild; every remaining error is a file that reached `RoleNames` through some other `using`. Note
that `MMCA.Common.Shared.Auth` still exists and still holds `AuthClaimTypes`, `RoleValue` and
`ClaimsPrincipalExtensions`, so do not delete that line, add yours beside it.

### 2. `TestPrincipal.Organizer()` becomes `TestPrincipal.InRole(role)`

| Old | New |
| --- | --- |
| `TestPrincipal.Organizer()` | `TestPrincipal.InRole(RoleNames.Organizer)` |
| `TestPrincipal.Organizer("7")` | `TestPrincipal.InRole(RoleNames.Organizer, "7")` |

The identity's display name is `"Test User"` rather than `"Organizer User"`; assert on the role, not
on that name. `TestPrincipal.AuthenticatedUser(...)` is unchanged.

### 3. `OwnerOrAdminFilterOptions.BypassRole` must be configured

`BypassRole` lost its `"Admin"` default and is `[Required]`. `AddAPI` registers the options with
`ValidateDataAnnotations()` and deliberately NOT `ValidateOnStart()`, so a host that never applies
`OwnerOrAdminFilter` needs no configuration at all, while a host that applies it without naming the
role fails on the first resolve with the data-annotation message. If you use the filter, add:

```csharp
services.Configure<OwnerOrAdminFilterOptions>(options => options.BypassRole = RoleNames.Admin);
```

`OwnershipHelper` lost the matching parameter defaults, so every call passes the role:

| Old | New |
| --- | --- |
| `OwnershipHelper.IsAdmin(currentUser)` | `OwnershipHelper.IsAdmin(currentUser, RoleNames.Admin)` |
| `GetOwnershipSpecification<TSpec, TId>(u, claimType, factory)` | `GetOwnershipSpecification<TSpec, TId>(u, claimType, factory, RoleNames.Admin)` |
| `GetOwnershipSpecification<TSpec>(u, factory)` | `GetOwnershipSpecification<TSpec>(u, factory, RoleNames.Admin)` |

Prefer reading the value from `IOptions<OwnerOrAdminFilterOptions>` where one is available, so the
role is stated once.

### 4. `TokenService` takes the permission registry

`TokenService`'s constructor gained a required `IPermissionRegistry` parameter (second position,
before the optional `TimeProvider` and `IOptions<JwksSettings>`). Resolving `ITokenService` from DI
needs no change: `IPermissionRegistry` always has a registration. Only code that constructs the
service by hand is affected:

| Old | New |
| --- | --- |
| `new TokenService(jwtOptions)` | `new TokenService(jwtOptions, permissionRegistry)` |
| `new TokenService(jwtOptions, timeProvider, jwksOptions)` | `new TokenService(jwtOptions, permissionRegistry, timeProvider, jwksOptions)` |

In a test, `new PermissionRegistryBuilder().Build()` is the grant-nothing registry.

Every access token now carries one `permission` claim per permission the registry grants the token's
role. That is additive: no existing claim changes, and a host that declared no grants mints exactly
the token it minted before. It is what lets a navigation entry gate on
`NavItem.RequiredPermission` instead of a role name, and the framework's own push-notification entry
now does (`notifications:manage`). Grant that permission to whichever role used to see the entry:

```csharp
services.AddPermissions(permissions => permissions
    .Grant(RoleNames.Organizer, NotificationPermissions.Manage));
```

## [1.195.0] - 2026-09-11

**The outbox table gains four nullable columns: add one migration per relational outbox source.**
`OutboxMessage` now carries the ambient context of the request that raised the event, so the
delivery can restore it. The columns are:

| Column | Type | Nullable |
| --- | --- | --- |
| `TenantId` | `varchar(64)` (the tenant column width the framework uses everywhere) | yes |
| `UserId` | the identifier type your host uses for users (`int` by default) | yes |
| `UserRoles` | `varchar(512)` | yes |
| `CorrelationId` | `varchar(64)` | yes |

This is expand-only (ADR-057): every column is nullable, nothing is renamed or dropped, and rows
written before the upgrade read back as "nothing was captured", which is exactly what they are. A
host can therefore deploy the migration and the new package in either order.

Generate the migration the way you generate every other per-source migration:

```sh
dotnet ef migrations add AddOutboxOriginColumns \
  --project <YourMigrationsProject> --startup-project <YourApiProject> \
  -- --datasource <Name>
```

Repeat it for each relational data source that owns an `OutboxMessages` table (every source in use,
under database-per-service). Cosmos sources need nothing: `CosmosDbContext` does not map the outbox.
A host with no `DataSources` section has exactly one source and therefore one migration.

Nothing else is required. `OutboxMessage.FromDomainEvent(domainEvent)` still compiles and still
stores nulls; the framework's own three call sites pass the captured origin for you.

**Three shipped signatures gained an optional parameter: recompile, change nothing.** Every existing
call site still compiles and still behaves as it did, because each added parameter is optional and
its default reproduces the old behaviour. What changed is the binary signature, so an assembly
compiled against 1.194.0 must be rebuilt rather than dropped in beside the new packages.

| Old | New | Effect of omitting the new argument |
| --- | --- | --- |
| `BrokerMessageBus(publishEndpoint)` | `BrokerMessageBus(publishEndpoint, currentUserService = null, tenantContext = null, correlationContext = null)` | No `MMCA-*` headers are stamped |
| `IntegrationEventConsumer<TEvent>(handlers, inbox, logger)` | `IntegrationEventConsumer<TEvent>(handlers, inbox, logger, serviceProvider = null)` | No publisher context is restored on the consumer scope |
| `OutboxMessage.FromDomainEvent(domainEvent)` | `OutboxMessage.FromDomainEvent(domainEvent, origin = default)` | The four context columns are stored as nulls |

The container resolves all three, so a host that registers them the normal way
(`AddInfrastructure`, the MassTransit consumer registration, the framework's own outbox write sites)
gets the new arguments filled in and needs no edit at all.

**Feature-flag lifecycle: a two-step opt-in, and nothing breaks if you skip it.** The new
`[FeatureFlag]` attribute and the `FeatureFlagLifecycleTestsBase` fitness pair are additive: a
consumer that changes nothing keeps building exactly as before. To adopt the gate (ADR-031):

1. Subclass the base in your architecture-test project, beside the other governance subclasses:

   ```csharp
   public sealed class FeatureFlagLifecycleTests : FeatureFlagLifecycleTestsBase
   {
       protected override IArchitectureMap Map { get; } = new StoreArchitectureMap();
   }
   ```

   Override `protected virtual DateOnly Today` if you would rather pin the judgement date than let
   the build's clock decide when a toggle goes red.

2. Annotate every `public const string` on every `*Features` class the map's Shared assemblies carry
   (`CatalogFeatures`, `SalesFeatures`, `ConferenceFeatures`, `EngagementFeatures`, ...). A flag that
   is a capability switch is `Permanent` and must NOT set `RemoveBy`; a flag that covers a rollout is
   `Temporary` and MUST set a parseable ISO `yyyy-MM-dd` one:

   ```csharp
   [FeatureFlag(FeatureFlagLifetime.Permanent, Owner = "Catalog")]
   public const string Recommendations = "Catalog.Recommendations";

   [FeatureFlag(FeatureFlagLifetime.Temporary, RemoveBy = "2026-12-31", Owner = "Catalog")]
   public const string NewPricingEngine = "Catalog.NewPricingEngine";
   ```

   Do step 2 first if you want a green build at every commit: subclassing the base before the
   constants are annotated is a deliberate red that names each unannotated flag.

A temporary flag going past its date fails the build on purpose. The fix is to delete the flag and
the branch it no longer chooses between, not to push the date out.

## [1.194.0] - 2026-09-11

**`MudToastService` no longer takes an `IAccessibilityAnnouncer`: nothing to do unless you construct
it yourself.** The optional second constructor parameter added in 1.193.0 is removed together with
the toast-text mirroring it drove. Toasts are now announced because `MmcaThemeProviders` hosts
`MudSnackbarProvider` inside a `role="status" aria-live="polite"` element, so the rendered toast is
the live-region content and a second copy of the text is neither needed nor wanted (it announced
every message twice and made `GetByText("...")` match two elements in Playwright).

The type is `internal` and is resolved through `IToastService`, so a host that registers it the
normal way (`AddUIShared`, or the `AddCommonUiFacades` bUnit base) is unaffected. Only code that
newed it up explicitly, inside this framework's own test assemblies or through `InternalsVisibleTo`,
sees a compile error, and the fix is one argument:

| Old | New |
| --- | --- |
| `new MudToastService(snackbar, announcer)` | `new MudToastService(snackbar)` |

`IAccessibilityAnnouncer` itself is unchanged and stays registered (`NullAccessibilityAnnouncer` by
default, `BrowserAccessibilityAnnouncer` once the device capabilities are added,
`MauiAccessibilityAnnouncer` on MAUI). Inject it wherever you want to announce something of your
own; just do not use it to repeat text that is already in the DOM.

## [1.192.0] - 2026-09-11

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

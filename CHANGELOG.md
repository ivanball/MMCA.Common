# Changelog

All notable changes to the MMCA.Common packages are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow [Semantic Versioning](https://semver.org/)
and are derived from git tags by MinVer (see [VERSIONING.md](VERSIONING.md)).

## [Unreleased]

### Added (2026-07-10 runtime performance wave, [ADR-040](ADRs/040-authenticated-output-caching-for-public-reads.md))
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

### Changed (2026-07-09 domain rejection messages in error toasts, [ADR-027](ADRs/027-multi-locale-i18n.md) Decision 9 carve-out)
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

### Added (2026-07-09 live channels, [ADR-039](ADRs/039-live-channel-push.md))
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

### Changed (2026-07-05 TimeProvider seams C-6/C-7)
- **`OutboxCleanupService` gains an optional trailing `TimeProvider` constructor parameter** (C-6,
  non-breaking, defaults to `TimeProvider.System`): the hour-scale sweep interval and the retention
  cutoff run on the injectable clock, making the purge sweep deterministically unit-testable with
  `FakeTimeProvider` (which the new sweep tests do).
- **`SessionCookieAuthenticationHandler` checks JWT expiry against the handler's `TimeProvider`**
  (C-7) instead of `DateTime.UtcNow`; no constructor change (set `options.TimeProvider` in tests
  for a deterministic clock).

### Added (2026-07-04 user-preferences E2E base, §14/§27/§28)
- **`UserPreferencesTestsBase`** (`MMCA.Common.Testing.E2E`, `Workflows.Preferences`): three
  self-contained facts consumers inherit with a one-line subclass: Spanish culture switch with
  cookie persistence across reload (probed via the shared /login "Welcome Back" heading), dark-mode
  toggle asserting the emitted `--mud-palette-background` variable flips to the PaletteDark value
  and persists across reload, and a 390px-viewport fact pinning the v1.103.0 mobile top-row
  controls in real apps (not just the gallery). No app-specific overrides needed.

### Fixed (2026-07-04 logout-then-login race, remaining site)
- **`ProfileManagementTestsBase.ChangePassword_WithValidCurrentPassword_ShouldSucceed` is now
  navigation-safe**: it waited on `LoadState.Load` after the sign-out click (already fired for the
  current document), so the re-login raced the in-flight logout forceLoad and died with
  `net::ERR_ABORTED` / "interrupted by another navigation" on contended runners (deterministic on
  Store's v1.104.1 e2e-gate). Now waits for the `/login` URL, the same fix v1.103.1 applied to
  `UserLoginTestsBase`; this was the one remaining sign-out-then-login site on the racy pattern.

### Fixed (2026-07-04 warning-chip contrast, §20/§22)
- **Filled Warning components now meet WCAG 2.1 AA in both palettes** (`MMCATheme`): MudBlazor's
  default white contrast text is ~2.65:1 on the light palette's `#F57F17` (and ~2.0:1 on the dark
  palette's `#FFA726`); `WarningContrastText` is now dark in both palettes (~7.9:1 / ~10.8:1, the
  standard Material treatment on amber). Latent until Store's new Buy Now E2E put a "Pending
  Payment" chip on the gated admin-order-list axe scan. Visual change: warning chips/buttons
  render dark-on-amber instead of white-on-amber.

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

### Fixed (2026-07-04 mobile parity)
- **Culture + theme controls are now reachable on phones (§22 / ADR-027/028).** The shared layout
  hides the whole `MudAppBar` below 1024px, and the `CultureSwitcher`/`ThemeToggle` lived only there,
  so no phone user (anonymous or signed-in) could switch language or theme. `NavMenu`'s mobile
  top-row now renders both controls unconditionally (module app-bar components and the user name
  stay auth-gated); existing top-row CSS handles compact sizing, white icons, and the desktop hide,
  so nothing renders twice. Pinned by `MobileTopRowE2ETests` (phone + desktop viewports) in the
  required chromium `ui-e2e` job.

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
  complementing the `IAnonymizable` erasure seam. Covered by `PiiRedactorTests` (incl. "never emits the
  clear-text PII values").

## [1.83.0] - 2026-06-26

Governance + front-end security hardening. No breaking changes.

### Added
- **ADR-023 — centralized security-response headers (§26).** Documents the hardened security-headers
  middleware + pluggable `ICspPolicyProvider` CSP seam (`AddCommonSecurityHeaders`), replacing per-host
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
v1.80.0 rate-limiter and `TimeProvider` seams. Additive — no breaking changes and no consumer behavior
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

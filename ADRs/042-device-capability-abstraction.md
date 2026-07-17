# ADR-042: Device Capability Abstraction (MAUI Blazor Hybrid)

## Status
Accepted (2026-07-10, amended 2026-07-17).

## Context
The consumer apps ship the same Blazor component set through three heads: MAUI Blazor Hybrid
(Android/iOS/MacCatalyst/Windows), Blazor Server SSR, and WebAssembly. Native device capabilities
(share sheet, clipboard, haptics, geolocation, local notifications, connectivity, biometrics,
text-to-speech) only exist on the MAUI head, and some have partial browser equivalents
(`navigator.share`, `navigator.clipboard`, `navigator.onLine`, aria-live regions). Shared Razor
components cannot reference MAUI APIs: `MMCA.Common.UI` is a single-target net10.0 Razor class
library that must stay WASM-compatible (its layer rule allows Shared only), and any MAUI-typed code
in it would break the web heads at compile time.

Two constraints shape the packaging. First, per-head service selection already has a working
precedent: `ITokenStorageService` is implemented by each host and registered after `AddUIShared`
(`MMCA.ADC/Source/Hosts/UI/MMCA.ADC.UI/MauiProgram.cs:99`). Second, MMCA.Common's CI and release
pipelines run on ubuntu-latest, which cannot build MAUI target frameworks at all, while the
framework's packages release in lockstep (ADR-016).

## Decision
Add a per-capability contract layer to `MMCA.Common.UI` and a fifteenth package,
`MMCA.Common.UI.Maui`, carrying the native implementations.

- **One small interface per capability, no aggregate device service.** Contracts live in
  `Source/Presentation/MMCA.Common.UI/Services/Capabilities/` (18 at introduction:
  `IShareService`, `IClipboardService`, `IHapticFeedbackService`, `IMapNavigationService`,
  `IGeolocationService`, `IExternalLinkService`, `ITextToSpeechService`, `IAccessibilityAnnouncer`,
  `ILocalNotificationService`, `IScreenshotService`, `IDevicePreferences`, `IBatteryStatusService`,
  `IBiometricAuthenticator`, `ISpeechToTextService`, `IExternalAuthBroker`, `IDeepLinkDispatcher`,
  `IConnectivityStatusService`, `ILocalCacheStore`). Each capability has an independent fallback
  story, and per-capability contracts let heads adopt incrementally.

- **Safe defaults for every contract, TryAdd-registered by `AddUIShared`.**
  `AddDeviceCapabilityDefaults` (`Source/Presentation/MMCA.Common.UI/Services/Capabilities/DependencyInjection.cs`)
  registers a null or neutral implementation per contract (`Fallbacks/`), so shared components
  resolve every capability on every head with zero host changes. Three fallback flavors:
  browser-equivalent (JS interop), null-object exposing `IsSupported == false` (components hide the
  affordance), and constant stubs (Blazor Server connectivity is always online: a dead circuit
  already means the whole UI is down).

- **Heads override AFTER `AddUIShared` with plain Add registrations.** Last registration wins for
  single-service resolution, so `AddBrowserDeviceCapabilities()` (browser implementations over
  `wwwroot/capabilities-interop.js`, prerender-safe per the `MauiBackNavigationBridge` degradation
  contract) and `AddMauiDeviceCapabilities()` / `UseMauiDeviceCapabilities()` (the MAUI package)
  replace the defaults without touching them.

- **`MMCA.Common.UI.Maui` is the one MAUI-TFM package.** It multi-targets
  net10.0-android/ios/maccatalyst/windows, references `MMCA.Common.UI` plus `Microsoft.Maui.Controls`
  and `Plugin.LocalNotification` (majors track MAUI majors, pinned together in
  `Directory.Packages.props`), and stays OUT of `MMCA.Common.slnx`, the same mechanism that keeps
  the UI gallery and E2E projects out of the ubuntu unit run. Dedicated windows jobs build
  (`ci.yml` `build-maui`, a required gate) and pack (`release.yml` `publish-maui`, same tag, own
  SBOM gate) it, so the lockstep release stays whole. Layer rule: UI + Shared only
  (`Source/Build/MMCA.Common.LayerEnforcement.targets`, `EnforceUIMauiLayerBoundary`); it is
  deliberately absent from the ubuntu NetArchTest runtime map because its assemblies cannot load
  there, and the compile-time target is the enforcement.

- **Deep links funnel through one seam.** Native navigation sources (notification taps, app
  actions, app links, QR scans) publish app-relative routes into the singleton
  `IDeepLinkDispatcher` (buffered, capacity one, for cold starts); the `DeepLinkListener`
  component in the shared layout drains the buffer after first render and navigates live requests.
  The payoff of Blazor Hybrid: web URLs and app routes are identical, so no translation table
  exists anywhere.

- **Shared components adapt, never branch on platform.** `ExternalLink` renders a real new-tab
  anchor on web heads and intercepts the click into the system browser where
  `IExternalLinkService.InterceptsLinks` is true, because `target="_blank"` silently dead-ends
  inside a BlazorWebView. `OfflineBanner` renders only when `IConnectivityStatusService` reports
  offline. Permission flows live INSIDE each MAUI implementation (check, rationale, request,
  degrade, never throw); components never see permission state.

## Rationale
- A god `IDeviceCapabilities` interface would force every head to implement everything and turn
  each new capability into a breaking change; per-capability contracts are open/closed and mock
  cleanly in bUnit.
- Null defaults inside `AddUIShared` mean a head that knows nothing about capabilities keeps
  working, which is exactly the inert-by-default posture of ADR-024's notification layer.
- Putting implementations in a framework package (not each app's MAUI head) gives the Store and
  ADC heads the same capabilities for one registration line, and keeps the ADR-015 discipline:
  reusable infrastructure belongs to Common.
- The windows-job packaging exception is the smallest change that preserves both truths: ubuntu CI
  stays fast and MAUI TFMs get built at all.

## Trade-offs
- A fifteenth package raises release surface: two runners must both succeed for a whole release.
  Accepted; the `publish-maui` job is gated by the same tag and SBOM discipline.
- The TryAdd-default plus later-plain-Add pattern relies on registration order (`AddUIShared`
  first). Documented on both extension methods; the order is already load-bearing in every host for
  other reasons.
- Null-object fallbacks can mask a missing registration (a MAUI head that forgets
  `UseMauiDeviceCapabilities()` silently loses haptics rather than failing fast). Accepted for
  decoration-grade capabilities; feature waves that depend on a capability assert `IsSupported` in
  their UI and surface the gap visibly.
- Biometrics, speech-to-text, and the external-auth broker now ship native MAUI implementations,
  all three registered by `AddMauiDeviceCapabilities()`
  (`Source/Presentation/MMCA.Common.UI.Maui/DependencyInjection.cs:44`, `:45`, `:59`). The residual
  trade-off is configuration, not code: `MauiExternalAuthBroker` registers unconditionally but
  reports `IsAvailable == false` (`Source/Presentation/MMCA.Common.UI.Maui/Capabilities/MauiExternalAuthBroker.cs:39`)
  until the head supplies `OAuth:MobileRedirectScheme`, so a misconfigured head quietly keeps the web
  anchor flow rather than failing fast.

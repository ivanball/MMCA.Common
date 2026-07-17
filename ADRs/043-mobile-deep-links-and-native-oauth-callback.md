# ADR-043: Mobile Deep Links, App Association, and the Native OAuth Callback

## Status
Accepted (2026-07-15). The framework leg is fully implemented in MMCA.Common: the OAuth
custom-scheme returnUrl allowlist in `CompleteAsync`, the app-association endpoint helper
`MapAppAssociationEndpoints`
(`Source/Presentation/MMCA.Common.API/Startup/AppAssociationEndpointExtensions.cs:35`, with
`AppAssociationOptions` alongside), and the MAUI `MauiExternalAuthBroker`
(`Source/Presentation/MMCA.Common.UI.Maui/Capabilities/MauiExternalAuthBroker.cs:19`). The ADC
consumer's deep-link wave has shipped: `MMCA.ADC.UI.Web` serves the two well-known association
documents through the shared helper
(`MMCA.ADC/Source/Hosts/UI/MMCA.ADC.UI.Web/Program.cs:162`), the Identity service allow-lists the
`atldevcon` scheme (`MMCA.ADC/Source/Services/MMCA.ADC.Identity.Service/appsettings.json:37`), and
the native heads register the callback: iOS carries both the custom-scheme URL type
(`MMCA.ADC/Source/Hosts/UI/MMCA.ADC.UI/Platforms/iOS/Info.plist:16`) and the associated-domains
entitlement (`MMCA.ADC/Source/Hosts/UI/MMCA.ADC.UI/Platforms/iOS/Entitlements.plist:11`), while
Android registers the custom-scheme `WebAuthenticatorCallbackActivity`
(`MMCA.ADC/Source/Hosts/UI/MMCA.ADC.UI/Platforms/Android/WebAuthenticatorCallbackActivity.cs:14`).
Android's AutoVerify https App Links intent filter is not yet in
`MMCA.ADC/Source/Hosts/UI/MMCA.ADC.UI/Platforms/Android/AndroidManifest.xml` (which today carries
only package-visibility queries), so the Android https app-link leg is still outstanding. MMCA.Store
has not adopted the wave: no association endpoints, allowlist config, or platform callback
registrations exist there yet.

## Context
Three mobile flows all need a URL to leave the web world and land inside the MAUI app:

1. **Shared links and QR codes.** The share sheet and QR codes carry ordinary https web URLs. With
   Android App Links (`assetlinks.json`) and iOS Universal Links (`apple-app-site-association`),
   the OS opens those URLs in the installed app instead of the browser.
2. **Notification taps and app actions**, which are app-internal and covered by the
   `IDeepLinkDispatcher` seam (ADR-042).
3. **External OAuth on a native head.** Google and GitHub reject OAuth inside embedded WebViews, so
   the MAUI head must run the provider flow in the system browser (`WebAuthenticator`) and needs
   the API to redirect its completion BACK to the app. Today `OAuthControllerBase.CompleteAsync`
   redirects only to the config-pinned `OAuth:UIBaseUrl`
   (`Source/Presentation/MMCA.Common.API/Controllers/OAuthControllerBase.cs`), which a native app
   cannot intercept.

The completion redirect design (ADR-036 lineage) has a hard security property worth preserving:
tokens never ride a redirect URL. `CompleteAsync` stashes the token pair server-side under a
single-use code and the UI exchanges it out-of-band via POST.

## Decision
- **Custom-scheme returnUrl allowlist in the framework.** `CompleteAsync` consults
  `OAuth:AllowedReturnUrlSchemes` (a config array, default empty). When the challenge's stashed
  `returnUrl` is an absolute URI whose scheme appears in the allowlist (for example
  `atldevcon://oauth-complete`), the completion redirect (and completion errors) target that URL
  instead of `OAuth:UIBaseUrl`, carrying only the same single-use code. The redirect echoes the
  client's `Uri.OriginalString` because URI normalization would append a trailing slash and native
  callback matching can be exact. `http`/`https` schemes never match even if listed, so web
  destinations always flow through the pinned base URL and the allowlist cannot become an open
  redirect. An empty allowlist reproduces the previous behavior byte for byte.
- **Client flow.** The MAUI head calls
  `WebAuthenticator` with `{gateway}/auth/oauth/{provider}?returnUrl={scheme}://oauth-complete`,
  captures `code` from the callback, POSTs the existing anonymous `/auth/oauth/exchange`, and
  stores the pair via `ITokenStorageService`. This rides behind the `IExternalAuthBroker` contract
  (ADR-042); the default broker is unavailable, which keeps the shared Login page on its anchor
  flow for web heads.
- **Association files are served by each app's UI.Web host**, not the gateway: the shared web URLs
  are UI-host URLs, and the gateway's `/.well-known` already forwards to Identity for JWKS.
  Explicit anonymous endpoints return `assetlinks.json` (with the PLAY APP SIGNING certificate
  fingerprint, not the local keystore's) and `apple-app-site-association` (team id + bundle id +
  the shared route paths). Platform side: `AutoVerify` intent filters on Android, the
  associated-domains entitlement on iOS, plus the `WebAuthenticatorCallbackActivity` /
  `CFBundleURLTypes` scheme registrations.
- **Incoming URIs reuse the ADR-042 dispatcher.** App-link and callback URIs are reduced to their
  path and query and published to `IDeepLinkDispatcher`; because all heads share one Blazor route
  table, no mapping layer exists.

## Rationale
- Reusing the single-use-code exchange keeps the token-never-in-URL invariant identical across web
  and native; the only new surface is WHERE the code lands.
- A scheme allowlist in configuration keeps the framework generic (Store can register its own
  scheme) while defaulting closed.
- Serving association files from the UI host keeps them next to the URLs they describe and out of
  the gateway's routing table.

## Trade-offs
- The app-facing hostname is baked into store binaries (intent filters, entitlements). The apps
  currently ride the Azure Container Apps default domain, which changes if the environment is ever
  recreated and would force store resubmissions. Every occurrence stays parameterized; a custom
  domain is the durable fix.
- A custom-scheme URI's host and path are attacker-choosable on a device with a hostile app
  registered for the same scheme (scheme hijack). Accepted: the redirect carries only a two-minute
  single-use code, the exchange is one-shot, and platform app-link verification does not exist for
  custom schemes anywhere.
- Completion failures that occur before authentication properties exist cannot know the native
  callback and still land on the web login page; the broker times out and the user retries.

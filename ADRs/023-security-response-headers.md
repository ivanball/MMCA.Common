# ADR-023: Centralized Security-Response-Headers Middleware with a Pluggable CSP

## Status
Accepted (2026-07-02).

## Context
Every client-facing host (the YARP Gateway and the Blazor UI web host in each app) must stamp the same
hardened HTTP response headers: `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`,
`Permissions-Policy`, HSTS, and a Content-Security-Policy. These were previously hand-rolled per host, so
the values drifted between Gateway and UI and between apps, and a new edge host could ship with weaker
headers (or none) by omission. A `Content-Security-Policy` is the hard part: an API or Gateway host that
serves JSON, WebSockets, and a static privacy page wants a strict, fixed policy, but a Blazor/MudBlazor
host needs `script-src 'wasm-unsafe-eval'` and `style-src 'unsafe-inline'` and must pin `connect-src` to
its own API/Gateway origin (which it only knows at runtime from configuration). One static policy cannot
serve both, and a wrong CSP hard-breaks the app, so the policy cannot simply be a constant in the
framework.

## Decision
Ship one security-headers middleware in `MMCA.Common.Aspire` (`MMCA.Common.Aspire.Security`), registered
with `AddCommonSecurityHeaders(configuration?, configure?)` and inserted early with
`UseCommonSecurityHeaders()`.

- **`SecurityHeadersMiddleware` stamps every response** with `X-Content-Type-Options: nosniff`,
  `X-Frame-Options` (default `DENY`), `Referrer-Policy` (default `strict-origin-when-cross-origin`),
  `Permissions-Policy` (default `geolocation=(), microphone=(), camera=(), payment=()`), and HSTS
  (`max-age=31536000; includeSubDomains`, emitted only outside Development and only when `EnableHsts`).
  All values are overridable via the `"SecurityHeaders"` configuration section or the `configure`
  delegate (`SecurityHeadersSettings`).
- **The CSP is resolved through an `ICspPolicyProvider` seam**, not stamped as a constant. The provider
  returns a `CspPolicy(string Value, bool Enforce)`: when `Enforce` is true the middleware writes
  `Content-Security-Policy`, otherwise `Content-Security-Policy-Report-Only`. Returning `null` emits no
  CSP.
- **The default provider (`StaticCspPolicyProvider`) returns a conservative baseline** from
  `SecurityHeadersSettings.ContentSecurityPolicy`:
  `default-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'`.
  It **deliberately omits `script-src` and `style-src`** so that an HTML host which forgets to register a
  fuller provider is not hard-broken (Blazor needs `script-src 'wasm-unsafe-eval'`, MudBlazor needs
  `style-src 'unsafe-inline'`). This baseline is the right complete policy for JSON/WebSocket/static
  hosts (API, Gateway) and a safe-but-partial policy for an unconfigured HTML host.
- **HTML hosts register their own `ICspPolicyProvider`** before calling `AddCommonSecurityHeaders`
  (the registration uses `TryAddSingleton`, so the first-registered provider wins). Both apps register
  one shared `BlazorCspPolicyProvider` (a single `internal sealed` class hoisted into
  `MMCA.Common.UI.Web`, byte-identical to the copies the app hosts formerly carried) via
  `AddCommonBlazorCsp` ahead of `AddCommonSecurityHeaders`. It pins `connect-src` to `'self'` plus the
  configured API/Gateway origin (https + wss, from the shared `ApiSettings`), adds `script-src 'self'
  'wasm-unsafe-eval'` and `style-src 'self' 'unsafe-inline'`, and **degrades to a permissive
  `Report-Only` policy if the origin cannot be resolved** (never enforce a CSP it could not build
  correctly). It loosens the policy for localhost only in Development (Visual Studio Browser Link / Hot
  Reload).
- **Adopted at both edges of both apps:** Store and ADC each wire `AddCommonSecurityHeaders` +
  `UseCommonSecurityHeaders` in their Gateway host and their UI web host, with the UI host also
  registering `BlazorCspPolicyProvider`. The middleware carries a unit test
  (`SecurityHeadersMiddlewareTests` in `MMCA.Common.Aspire.Tests`).

## Rationale
- **One hardened default, defined once.** Centralizing the header set removes per-host drift and makes a
  new edge host secure by default rather than by remembering to copy headers.
- **A seam, because one CSP cannot fit all hosts.** The `ICspPolicyProvider` indirection is the minimum
  needed to let a Blazor host inject a runtime, origin-pinned policy while API/Gateway hosts keep the
  strict static one, without the framework guessing either app's origins.
- **Fail-safe over fail-secure for the default.** Omitting `script-src`/`style-src` from the baseline
  trades a slightly weaker default CSP for the guarantee that the shared middleware can never be the
  thing that blanks out a Blazor app. A host that wants the strong policy opts in by registering a
  provider, which is visible and testable.
- **Report-Only as the degradation path.** A dynamic policy that cannot be built (misconfigured origin)
  downgrades to Report-Only instead of hard-breaking production, surfacing the misconfiguration in the
  browser console rather than as an outage.

## Trade-offs
- **The baseline CSP is intentionally incomplete.** An API/Gateway host gets `default-src 'self'`-style
  protection but no `script-src`/`style-src` discipline unless it registers a fuller provider; an HTML
  host that forgets to register one runs without script/style restrictions (safe, but not the intended
  hardened policy). The omission is documented on `SecurityHeadersSettings.ContentSecurityPolicy`.
- **Registration order is a foot-gun.** Because the provider is registered with `TryAddSingleton`, a host
  must register its custom `ICspPolicyProvider` *before* `AddCommonSecurityHeaders`, or the static
  default wins silently.
- **A shared Blazor CSP provider constrains per-host divergence.** `BlazorCspPolicyProvider` now lives
  once in `MMCA.Common.UI.Web`, over the shared `ApiSettings` type, and both apps register it via
  `AddCommonBlazorCsp`, so the connect-src/origin logic is no longer copied per app. The remaining
  trade-off is that a host needing genuinely different CSP logic cannot edit an app-local class: it must
  supply its own `ICspPolicyProvider` (registered before `AddCommonSecurityHeaders`) instead.
- **A degraded (Report-Only) policy protects nothing.** The fail-safe path means a broken dynamic policy
  is non-blocking but also non-enforcing until someone notices the Report-Only header.

## Related
ADR-019 (rate limiting, the other always-on edge protection living in the same Aspire layer), ADR-022
(browser session-cookie auth, the other browser-edge security control), ADR-008 (the gateway topology
whose Gateway and UI hosts are where these headers are stamped).

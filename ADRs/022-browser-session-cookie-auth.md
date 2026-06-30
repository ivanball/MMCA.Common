# ADR-022: Browser Session-Cookie Authentication for Blazor SSR

## Status
Accepted.

## Context
The apps are Blazor Web Apps: a server-rendered (SSR) prerender pass runs on the first request, then
an interactive phase (Blazor Server or WebAssembly) takes over. Authentication against the API is
JWT-based (ADR-004): login returns an access token plus a refresh token, and the interactive client
sends the access token as a bearer header on API calls. That leaves a gap on **fresh GET requests**
the browser issues directly: a deep link, an F5 refresh, or "open in new tab" of an `[Authorize]`
page. At SSR-prerender time there is no interactive client yet and no `Authorization` header, so
`[Authorize]` would fail and bounce the user to `/login` even though they are logged in. Storing the
JWT in `localStorage` does not help: SSR runs on the server and cannot read it, and exposing the
refresh token to JavaScript is an XSS-exfiltration risk.

## Decision
Carry the session in **HttpOnly cookies** and add an authentication scheme that reads them during SSR
prerender. The mechanism ships in `MMCA.Common.API` (`SessionCookies/`) with a `MMCA.Common.UI`
companion, and both apps' Web UI hosts wire it.

- **Two HttpOnly cookies, seeded from JS at login.** `mmca_auth_access` (the JWT) and
  `mmca_auth_refresh` (the refresh token). The browser seeds them via `POST /auth/session-cookie` at
  login and clears them via `DELETE` at logout; `SessionCookieJar` writes them HttpOnly with
  `SameSite=Lax`.
- **An SSR-time scheme reads the access cookie.** `SessionCookieAuthenticationHandler` (scheme
  `"SessionCookie"`) reads `mmca_auth_access` during prerender, parses its claims, checks expiry, and
  populates `HttpContext.User`, so `[Authorize]` passes on fresh GETs. It **does not validate the
  signature**: the cookie was minted by the UI host from a token the API already issued, and the API
  still fully validates the JWT on every API call (ADR-004). The handler only lets ASP.NET Core read
  the identity for prerender; it is not the security boundary.
- **The refresh token never leaves the server.** `POST /auth/session/token` is a same-origin
  "validate-or-refresh" endpoint the browser calls to hydrate its **in-memory** access token;
  `CookieSessionRefresher` reads the HttpOnly refresh cookie server-side, refreshes if needed, and
  returns only the access token plus expiry. The refresh token is never exposed to JavaScript.
- **CSRF defense-in-depth.** All cookies are `SameSite=Lax`. The seed/clear endpoints (`POST` and
  `DELETE /auth/session-cookie`) deliberately disable antiforgery (they carry no antiforgery token) and
  rest on `SameSite=Lax` alone; the `/auth/session/token` refresh endpoint additionally rejects
  cross-site requests via the `Sec-Fetch-Site` header.

This is a backend-for-frontend (BFF) style token-storage layer: the browser holds an HttpOnly session
whose refresh half it cannot read, the SSR pass authenticates from the access cookie without trusting
it as the security boundary, and the real enforcement stays at the API.

## Rationale
- **Fixes the fresh-GET prerender gap.** Without a server-readable session, every deep-link or F5 to
  an `[Authorize]` page would redirect to `/login` despite a valid session; the cookie scheme closes
  that without a per-page workaround.
- **HttpOnly keeps the refresh token out of JS.** Storing the refresh token where a script can read it
  (localStorage) is the classic XSS-exfiltration risk; HttpOnly cookies plus a server-side refresh
  endpoint keep it inaccessible to scripts.
- **Skipping signature validation at SSR is safe because the API is the boundary.** The SSR handler
  only needs the user's claims to render; every state-changing or data-returning call goes to the API,
  which validates the JWT properly (ADR-004), so prerender does not need to re-verify a cookie the
  host itself minted.
- **Shared in the framework.** Both apps face the identical Blazor-Web-App prerender problem, so the
  scheme, the cookie jar, the endpoints, and the refresher live in MMCA.Common rather than being
  re-derived per app.

## Trade-offs
- **A non-validating auth scheme exists.** `SessionCookieAuthenticationHandler` trusts a cookie it does
  not cryptographically verify. This is sound only because (a) the cookie is HttpOnly and host-minted
  and (b) the API independently validates every call. Authorizing a sensitive action purely on the SSR
  principal without an API round-trip would break the safety argument.
- **Cookie and header dual path.** The session now lives both in cookies (browser GETs / SSR) and as
  an in-memory bearer token (interactive API calls); the two must stay in sync, which is what the
  `/auth/session/token` hydrate endpoint and the UI's `ISessionCookieSync` manage.
- **CSRF surface.** Cookie-based auth reintroduces CSRF considerations a pure bearer-header scheme
  avoids; mitigated by `SameSite=Lax` (plus the refresh endpoint's `Sec-Fetch-Site` check), but it is a
  surface a header-only design would not have.
- **Expiry is checked, not cryptographically enforced, at the SSR edge.** A tampered cookie yields
  claims that fail at the API on the next call, but the prerendered HTML for that one pass is produced
  from unverified claims.

## Related
ADR-004 (the JWT/JWKS validation the API performs on every call, which is why the SSR handler can skip
signature validation), ADR-008 (the gateway and topology the UI talks to), ADR-019 (rate limiting on the
auth surface), ADR-029 (the login brute-force protection this session seeds from).

# ADR-036: External OAuth Login (Federated Google/GitHub) with Local-JWT Exchange

## Status
Accepted (2026-07-02, migration attribution corrected 2026-07-06, native-callback redirect branch added 2026-07-17 per ADR-043).

## Context
The framework's Identity story so far is entirely first-party: a user registers with an email and
password, the credentials are hashed (ADR-032), and Identity mints its own RS256 JWT pair. Every auth
ADR to date sits inside that first-party boundary: ADR-004 validates Identity's own tokens across
extracted services, ADR-020 layers permissions over the token's role, ADR-022 carries the same token
in browser cookies, ADR-029 throttles the anonymous credential surface, ADR-032 hashes the password,
and ADR-033 checks resource ownership from a token claim. None of them lets a **third-party identity
provider** vouch for the user. The word "federated" already appears in the ADR set (ADR-007 / ADR-008),
but it means service-to-service JWKS trust between our own hosts, not a Google or GitHub account
signing a person in.

We wanted social sign-in (Google, GitHub) without giving up the invariant that the rest of the system
depends on: **inside the app, a user is always our local `User` carrying our own JWT.** External
identity should be an *entry path* that terminates in the same local token pair every other flow
produces, not a parallel identity system that downstream services would have to learn to validate. It
also had to be optional: most hosts, tests, and local dev runs have no OAuth secrets and must keep the
JWT-only pipeline untouched, the same inert-until-configured posture as `AddPermissions` (ADR-020).

## Decision
Add an opt-in external-login path that federates Google/GitHub sign-in at the edge and immediately
**exchanges the external identity for the app's own local JWT pair**, linking the external account to
a local `User`.

- **Scheme registration is config-gated per provider.** `AddExternalAuthProviders` reads the `OAuth`
  section and enables Google and/or GitHub only when that provider's `OAuth:<Provider>:ClientId` is
  present; with neither configured it returns without touching the pipeline, so `AddCommonAuthentication`'s
  JWT-only default is left exactly as it was. `AddAuthentication()` is called with no argument so it
  appends schemes rather than resetting the JWT bearer default. A configured `ClientId` with a missing
  `ClientSecret` fails fast at startup. This is the same signal the UI's `ConfigurationOAuthUISettings`
  uses to light up the Google/GitHub buttons (`GoogleEnabled` / `GitHubEnabled`).
- **A short-lived cookie carries the external principal, nothing more.** The provider callback signs
  into a dedicated `ExternalLogin` cookie scheme (`mmca_external_login`, HttpOnly, SameSite=Lax, a
  10-minute lifetime). It exists only to hand the external claims from the provider callback to the
  controller; it is never the app session. GitHub is asked for the `user:email` scope because its
  default scope omits the email the exchange needs.
- **`OAuthControllerBase.CompleteAsync` performs the exchange.** The challenge endpoints redirect to
  the provider and back to `CompleteAsync`, which authenticates the `ExternalLogin` cookie, extracts
  `(provider, providerKey, email, firstName, lastName)` from the standard claims, and calls
  `IAuthenticationService.ExternalLoginAsync`. On success it signs the external cookie out immediately,
  so the external principal lives no longer than the exchange.
- **Tokens never ride the redirect URL.** `CompleteAsync` mints a single-use opaque code, stashes the
  minted token pair server-side in the cache under a short TTL, and redirects carrying only that code.
  The redirect target defaults to `OAuth:UIBaseUrl`, but when the stashed `returnUrl` is an absolute URI
  whose custom scheme is allow-listed in `OAuth:AllowedReturnUrlSchemes` it targets that native-app URL
  instead, so a system-browser `WebAuthenticator` window captures the code and closes (ADR-043); an
  empty allowlist (the default) keeps the web-only behavior exactly, and http/https `returnUrl`s never
  match, so the allowlist cannot become an open redirect. Completion errors follow the same branch. The
  UI calls `ExchangeAsync` (`POST`) out of band to swap the code for the tokens; the
  code is burned on first use, and a missing, replayed, or expired code yields HTTP 400. Access and
  refresh tokens therefore never land in the address bar, browser history, the `Referer` header, or
  upstream access logs.
- **The exchange resolves to a local `User`, three ways.** `ExternalLoginAsync` (app-level) first
  looks the user up by `LoginProvider` + `ProviderKey`. Missing, it looks up by email and, if a local
  or other-provider account already owns that email, **links** the external provider to it
  (`User.LinkExternalProvider`) rather than rejecting the sign-in. Only when no account owns the email
  does it **create** a new `Attendee` via `User.CreateExternal` (an external user has empty password
  hash/salt and carries `LoginProvider` / `ProviderKey`). Either way it rotates the refresh token,
  saves, and mints the access token, so the caller receives the same `AuthenticationResponse` shape as
  a local login.
- **The linkage is two nullable columns and a filtered unique index.** `User.LoginProvider`
  (`varchar(50)`) and `User.ProviderKey` (`varchar(256)`) are null for local accounts;
  `IsExternalLogin` is derived from `LoginProvider is not null`. In ADC's per-service Identity database
  both columns and a unique index over the pair (filtered to non-null rows) are created by the Identity
  `InitialCreate` migration
  (`MMCA.ADC/Source/Hosting/MMCA.ADC.Migrations.SqlServer.Identity/Migrations/20260606053130_InitialCreate.cs:63`,
  index at `20260606053130_InitialCreate.cs:100`), so two external identities cannot map to the same
  local account while local (null,null) accounts are unconstrained. (The earlier standalone
  `AddExternalLoginProviderFields` migration survives only in the frozen combined single-DB archive
  under `MMCA.ADC.Migrations.SqlServer/` and is never applied to the per-service database.)
  `User.Anonymize` clears both fields on erasure (ADR-005).
- **New external users re-use the local registration side-effect.** A brand-new external `User`
  publishes the same post-commit `UserRegistered` integration event the local `RegisterAsync` path
  publishes, so Conference runs the BR-207 speaker email-match auto-link asynchronously. The first
  token does not yet carry `speaker_id`; it is picked up on the next refresh (the same eventual
  consistency the local path accepts).

The default lives in the framework but the behavior is app-supplied: the `IAuthenticationService`
interface member `ExternalLoginAsync` has a default implementation that returns an
`Auth.ExternalLoginNotSupported` failure, so a host that never wires the flow degrades safely rather
than silently succeeding.

Adoption is **ADC only**, and partial adoption is by design (ADR-018 / ADR-020 model the same "shipped
in the framework, adopted by one app" shape). ADC's Identity module supplies a sealed `OAuthController`
subclass carrying the class-level `[ApiController]` / `[Route("auth/oauth")]` / `[ApiVersion("1.0")]`
attributes (not reliably inherited), its `AuthenticationService` overrides `ExternalLoginAsync`, its
Identity service host calls `AddExternalAuthProviders`, and the AppHost passes the UI's HTTPS endpoint
to the Identity service as `OAuth__UIBaseUrl` so the post-exchange redirect lands on the UI host.
**MMCA.Store does not adopt external login:** it registers no OAuth schemes, defines no `OAuthController`,
adds no provider fields to its `User`, and leaves `ExternalLoginAsync` at the not-supported default. Its
Identity story stays local-credential + RS256 only.

## Rationale
- **Terminate federation at the edge, keep one internal identity.** Exchanging the external principal
  for a local JWT the moment the callback returns means every downstream concern (JWKS validation,
  permissions, ownership, session cookies) sees the token it already understands. External identity
  never leaks past the controller, so the rest of the system needs no changes.
- **Code-in-cache beats tokens-in-URL.** A redirect is a `GET` that the browser records and proxies
  log; putting the tokens behind a single-use, short-TTL, server-side code keeps credentials out of
  every place a URL is written down, at the cost of one extra out-of-band `POST`.
- **Link-by-email, do not fork the account.** Matching an incoming external email to an existing
  account and attaching the provider (rather than minting a second `User`) keeps a person's bookmarks,
  notifications, and speaker link on one identity regardless of how they signed in.
- **Inert until configured mirrors the framework's other opt-ins.** Gating each provider on its
  `ClientId` and defaulting `ExternalLoginAsync` to a not-supported failure means a host with no OAuth
  secrets (most tests, local dev, and Store) behaves exactly as before, the same posture as ADR-020.
- **The app owns account creation, the framework owns the handshake.** The OAuth handshake, cookie,
  and code exchange are generic and live in the framework; the `User` factory surface, default role,
  and claim set are app-specific and stay in the subclass, so the flow composes with each app's
  existing registration rules instead of duplicating them.

## Trade-offs
- **Opt-in per app, and easy to half-wire.** The flow needs four cooperating pieces (scheme
  registration, the controller subclass, the service override, and the `OAuth__UIBaseUrl` redirect
  target). A host that registers schemes but forgets the controller, or configures a `ClientId`
  without the matching UI flags, gets a broken or invisible button, the same audit-the-inventory
  caveat as ADR-020. Adopting it also requires the migration that adds the provider columns.
- **Email trust is inherited from the provider.** Link-by-email assumes the provider returns a
  verified email; a provider that returns an unverified address would let an external sign-in attach
  to an existing account. GitHub's `user:email` scope and Google's verified email are the mitigation,
  not a check the framework performs.
- **Not exactly-once account creation across the redirect.** The exchange commits the `User` before
  the UI redeems the code; an abandoned redemption still creates (or links) the account. That is the
  intended tradeoff (the identity is real once the provider vouched for it), but it means a completed
  challenge can leave a local account whose token pair was never collected.
- **A second credential shape on `User`.** External accounts carry empty password hash/salt and rely
  on `LoginProvider` / `ProviderKey`; code that assumes every `User` has a usable password must check
  `IsExternalLogin` first.

## Related
ADR-004 (the RS256/JWKS token this flow exchanges the external identity *for*, and validates
everywhere after), ADR-022 (the browser cookies that carry the resulting session), ADR-029 (the
brute-force protection on the first-party credential surface this flow sits beside), ADR-032 (the
password hashing external accounts deliberately skip), ADR-020 (the inert-until-configured opt-in
posture this mirrors), ADR-005 (`User.Anonymize` clears the provider fields on erasure), ADR-043 (the
native-app callback that adds the allow-listed custom-scheme redirect branch to this flow's
`CompleteAsync`).

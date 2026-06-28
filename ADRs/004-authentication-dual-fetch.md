# ADR-004: Cross-Service Token Validation via JWKS / OIDC Discovery

## Status
Accepted.

## Context
When the modular monolith is extracted into per-module service hosts behind a gateway (ADR-008),
every service must authenticate the same end-user JWT, but only one service (Identity) issues tokens.
In the monolith, issuer and validator are the same process, so a single symmetric secret
(HMAC-SHA256) suffices: the secret that signs a token also validates it. Once Identity is a separate
process, sharing that symmetric secret with every other service means every service can also *mint*
tokens, and rotating the secret becomes a coordinated multi-service change. We needed a way for
extracted services to validate Identity's tokens without holding any signing key material, and
without pinning configuration that breaks when the internal service-discovery hostname differs from
the public issuer URL.

## Decision
Validate cross-service tokens with **asymmetric (RS256) signatures plus JWKS / OIDC discovery**,
keeping the symmetric (HS256) shared-secret path as the in-process monolith default. The signing
mode is a single configuration switch (`JwtSettings.SigningAlgorithm`, default `HS256`).

**Issuer side (Identity service).**
- Identity signs access tokens with its RSA private key (RS256) and publishes only the matching
  public key (ADC's Identity service sets `"SigningAlgorithm": "RS256"`).
- `IJwksProvider` / `RsaJwksProvider` materialize a `JsonWebKeySet` from a PEM public key
  (`JwksSettings`: `Enabled` defaults to `false`, `KeyId` defaults to `"default"`, key supplied
  inline via `RsaPublicKeyPem` or by `RsaPublicKeyPath`). When publishing is disabled or no key is
  configured, the provider returns an empty key set, so the endpoint stays queryable.
- Two well-known endpoints are mapped centrally for every host (`MapJwksEndpoint`,
  `MapOidcDiscoveryEndpoint`), both anonymous: `/.well-known/jwks.json` and
  `/.well-known/openid-configuration`. The discovery document advertises the validation-relevant
  `issuer` + `jwks_uri` (plus minimal OIDC metadata: `response_types_supported`,
  `subject_types_supported`, `id_token_signing_alg_values_supported`) and returns `404` when
  `Jwt:Issuer` is unset, so a non-Identity host serving the same route exposes nothing.

**Validator side (every other service).**
- `AddForwardedJwtBearer(authority, audience)` points the JWT bearer middleware at an `Authority`, so
  it fetches `{authority}/.well-known/openid-configuration`, follows `jwks_uri`, and validates the
  token signature against the published key. No service except Identity holds key material. ADC's
  Conference, Engagement, and Notification services all use this path.
- `ValidIssuer` is deliberately **not** pinned: the middleware takes the issuer from the discovery
  document, because the `authority` is the Aspire service-discovery URL (e.g. `http://identity`)
  while the token's `iss` claim is the public gateway origin (e.g. `https://localhost:6001`).
- Both validators pin `TokenValidationParameters.ValidAlgorithms` so an attacker cannot force an
  algorithm swap (for example, signing an HS256 token using the RSA public key as the HMAC secret):
  the **forwarded JWKS** validator (`AddForwardedJwtBearer`) pins `[RS256]` (the JWKS path only ever
  validates Identity's asymmetric tokens), and the **in-process** validator
  (`AddCommonAuthentication` → `BuildValidationParameters`) pins `[RS256]` for the asymmetric path or
  `[HS256]` for the symmetric one.
- `AddCommonAuthentication(configuration)` remains the in-process path: HS256 with the shared Base64
  secret (monolith default), or RS256 validating against a locally configured public-key PEM (no
  JWKS fetch). It requires `RsaPublicKeyPem` when RS256 is selected and directs extracted services to
  `AddForwardedJwtBearer` instead.

**Discovery routing and fallback.**
- Discovery is wired by the AppHost helper `WithJwksDiscovery(identity, gateway?)`. Because the
  extracted REST services listen HTTP/2-only on cleartext for h2c gRPC (ADR-012), the default
  HTTP/1.1 JwtBearer backchannel cannot reach Identity directly, so the authority is set to the
  **gateway** HTTPS origin; the gateway terminates TLS, speaks HTTP/1.1 and HTTP/2 via ALPN, and
  forwards `/.well-known/*` on to Identity. When no gateway is passed, it **falls back** to Identity's
  HTTPS endpoint directly (HTTP/2-only, so the gateway form is preferred). ADC's AppHost uses the
  two-argument gateway form for all three validating services.
- `OpenIdConnectMetadataWarmupTask` (MMCA.Common.Aspire) pre-fetches the discovery document at
  startup so the first authenticated request on a cold, CPU-throttled replica does not pay the
  discovery round trip inside the request (the classic "first request fails, second succeeds" pattern
  on Azure Container Apps Consumption).
- SignalR cannot send an `Authorization` header on the WebSocket upgrade, so both registration paths
  read the token from the `access_token` query string for `/hubs` requests.

## Rationale
- **No shared signing key.** Only Identity can mint tokens; every other service holds only the public
  key it fetched, so a compromised non-Identity service cannot forge tokens, and key rotation is
  publish-once at the issuer.
- **Discovery over hard-coded keys.** Fetching the key set via OIDC discovery means consumers need no
  per-service key configuration, and rotation does not require redeploying every validator.
- **Origin-aligned issuer.** Deriving the issuer from the discovery document rather than pinning it
  keeps validation working when the internal hostname and the public issuer differ, which is the
  normal case behind a gateway.
- **HS256 stays the monolith default.** A single-process deployment needs no asymmetric keys or JWKS
  endpoint; the algorithm switch lets the same code run either way.

## Trade-offs
- **More moving parts than a shared secret.** RS256 needs key generation, distribution of the public
  half, a JWKS endpoint, and discovery wiring, versus one symmetric string.
- **Discovery is a startup dependency.** A validator cannot verify tokens until it has fetched the key
  set; the warmup task and the resilience pipeline (ADR-009) absorb the cold-start and transient
  cases, and the middleware caches the key set after the first fetch.
- **Transport coupling.** The HTTP/2-only cleartext endpoints (ADR-012) force discovery through the
  gateway; the direct-Identity fallback exists but is HTTP/2-only, so callers should prefer the
  gateway form.
- **Endpoint hygiene is on the issuer.** JWKS and discovery are anonymous by definition; the discovery
  doc returns `404` on non-issuer hosts and the JWKS provider returns an empty set when unconfigured,
  so only the real issuer advertises a key.

## Related
ADR-007 (gRPC calls forward the validated JWT downstream via `JwtForwardingClientInterceptor`),
ADR-008 (the extraction that split issuer and validator into separate processes), ADR-012 (the
HTTP/2-only transport that forces gateway-routed discovery), ADR-009 (the resilience pipeline that
covers the discovery fetch).

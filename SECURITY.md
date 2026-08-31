# Security Policy

## Supported versions

MMCA.Common is released as a single set of lockstep-versioned packages on one version line (the
count is owned by [FACTS.md](FACTS.md); policy in [the published versioning policy](https://ivanball.github.io/docs/guides/common-VERSIONING.html)). Security fixes
are applied to the latest released version; there
is no long-term support branch — upgrade to the latest patch.

## Reporting a vulnerability

Please report suspected vulnerabilities **privately**, not via public issues:

- Preferred: open a private [GitHub Security Advisory](https://github.com/ivanball/MMCA.Common/security/advisories/new).
- Or email the maintainer (see repository owner).

Include affected version, a description, and a reproduction if possible. Please allow time for a
fix before public disclosure.

## Security model (what the framework provides)

- **Authentication:** JWT bearer with RS256 and JWKS discovery (`/.well-known/jwks.json`); the
  signing algorithm is pinned (no `alg:none` / HS/RS confusion). `AddForwardedJwtBearer` resolves
  `RequireHttpsMetadata` secure-by-default, in three steps: the explicit argument when the caller
  passes one, then the `Authentication:JwtBearer:RequireHttpsMetadata` configuration key, then
  `true` in every environment except Development. A resolved `false` outside Development stays
  legal (an internal-ingress cleartext `h2c` authority is the reason it must) but logs one warning
  at startup naming the configuration key, so the opt-out is auditable instead of silent.
- **Password hashing:** PBKDF2-SHA512 with a high iteration count and constant-time comparison. Both
  are build-failing invariants, not conventions: `PasswordHasherSecurityTests` recomputes the digest
  independently (known-answer tests, plus a negative one proving the work factor participates in
  verification) and pins the iteration count, salt size and digest size by reflection, while
  `PasswordHashingFitnessTests` asserts structurally that the hasher still depends on a slow key
  derivation function and on the fixed-time comparison. PBKDF2 is the only verification path: there is
  no legacy HMAC fallback.
- **Field encryption:** AES-256-GCM via `EncryptedStringConverter` for sensitive columns.
- **Authorization:** server-side; `Result` → HTTP status mapping never leaks internal detail.
- **CORS:** the permissive `AllowAnyOrigin` policy is **development-only**; production uses an
  explicit allow-list with `AllowCredentials` (the two are never combined, which browsers reject
  and which is insecure).
- **Idempotency & rate limiting** primitives are provided for consumers to apply at the edge.

## Dependency & supply-chain security

- All package versions are centrally pinned; **NuGet lock files** are committed.
- **Vulnerability auditing** runs in CI (`dotnet list package --vulnerable --include-transitive`)
  and as a build-time gate (`NuGetAudit`), with `TreatWarningsAsErrors` promoting advisories to
  build failures.
- `nuget.config` **package source mapping** restricts every package to nuget.org (dependency-
  confusion defense).
- A **CycloneDX SBOM** is produced at release.
- `MassTransit` is pinned to v8 (v9 requires a commercial license); a fitness test enforces this.

## Security invariants enforced as tests

These used to be listed as consumer responsibilities the framework could not check. They now run as
executable fitness functions:

- **No unintended `[AllowAnonymous]`.** `AnonymousEndpointTestsBase`
  (`MMCA.Common.Testing.Architecture`) scans MVC controllers and routable Blazor components by
  reflection and fails on any `[AllowAnonymous]` that is not in the subclass's explicit allow-list,
  fails on a stale allow-list entry, and fails when the scanned set is empty. MMCA.Common runs it
  over its own API and UI assemblies; consumers subclass it per repo. Limitation: minimal-API
  `.AllowAnonymous()` is endpoint metadata, invisible to static reflection, so the framework's
  intentional minimal-API anonymous surface (JWKS, OIDC discovery, app-association, session-cookie
  refresh, health) is out of its reach.
- **No `AllowAnyOrigin` combined with `AllowCredentials`.** Both CORS registrations execute in unit
  tests that assert the split: the permissive allow-any policy never supports credentials, the
  credentialed policy never widens to any origin, and a missing `Cors:AllowedOrigins` section fails
  closed.
- **The signature algorithm stays pinned** to RS256 on both validation paths, asserted against the
  options the registration code actually produces.
- **`RequireHttpsMetadata` stays secure-by-default**, with the resolution order above asserted per
  environment.

## Consumer responsibilities (not enforceable in this framework)

Some invariants still depend on the consuming application's own code and deployment:

- Server-side authorization on every non-public endpoint (UI hiding is not authorization).
- Secrets in a vault / managed identity, never in source or plain config.
- A justification recorded beside any `Authentication__JwtBearer__RequireHttpsMetadata=false` in a
  deployment template.

## OWASP Top 10

The framework has been reviewed against the OWASP Top 10. The most relevant categories
(A01 Broken Access Control, A02 Cryptographic Failures, A05 Security Misconfiguration,
A06 Vulnerable Components) are addressed by the controls above; injection (A03) is mitigated by
parameterized EF Core access only.

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
  signing algorithm is pinned (no `alg:none` / HS/RS confusion). `RequireHttpsMetadata` is a
  caller-supplied setting (enable it in production).
- **Password hashing:** PBKDF2-SHA512 with a high iteration count and constant-time comparison.
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

## Consumer responsibilities (not enforceable in this framework)

Some invariants depend on the consuming application's endpoints and configuration, and are best
enforced by the consumer's own architecture tests:

- No unintended `[AllowAnonymous]` on protected endpoints (the framework's anonymous endpoints —
  login/register/refresh, JWKS, OIDC discovery — are intentional).
- No `AllowAnyOrigin` combined with `AllowCredentials` in production CORS.
- Server-side authorization on every non-public endpoint (UI hiding is not authorization).
- Secrets in a vault / managed identity, never in source or plain config.

## OWASP Top 10

The framework has been reviewed against the OWASP Top 10. The most relevant categories
(A01 Broken Access Control, A02 Cryptographic Failures, A05 Security Misconfiguration,
A06 Vulnerable Components) are addressed by the controls above; injection (A03) is mitigated by
parameterized EF Core access only.

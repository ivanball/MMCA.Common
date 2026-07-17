# ADR-032: Password Hashing (PBKDF2-HMAC-SHA512) with Legacy-Hash Backward Compatibility

## Status
Accepted (2026-06-29, adoption note revised 2026-07-06).

## Context
Identity stores a credential as a (salt, hash) pair, never plaintext. The framework needs one
canonical hasher that every consuming Identity flow shares, so the key-derivation algorithm, work
factor, and comparison are decided once and not re-implemented per app. Two forces shape the choice:

- **A modern, deliberately slow KDF.** A fast hash (or a raw HMAC) makes offline brute force cheap if a
  credential table leaks, so the new format must use a stretched, salted derivation tuned to current
  guidance, with a timing-safe comparison.
- **An existing corpus of already-stored passwords.** Earlier records were written with an older
  HMAC-SHA512 scheme. Those rows cannot be force-reset, and they cannot be re-hashed in place: the
  plaintext needed to recompute a hash only exists transiently at the user's next login. Verification
  therefore has to accept the old format *and* the new one from the same stored bytes, with no schema
  flag distinguishing them.

## Decision
Provide a single `IPasswordHasher` (`MMCA.Common.Application.Interfaces.Infrastructure`,
`IPasswordHasher.cs:6`) with one implementation `PasswordHasher`
(`Source/Core/MMCA.Common.Infrastructure/Services/PasswordHasher.cs:12`), registered unconditionally by
`AddInfrastructure` as a singleton (`Source/Core/MMCA.Common.Infrastructure/DependencyInjection.cs:186`,
inside the method at `DependencyInjection.cs:48`). The type is stateless (only `const` parameters), so
the singleton lifetime is safe.

- **New passwords use PBKDF2-HMAC-SHA512.** `HashPassword` draws a fresh 32-byte (256-bit) salt from
  `RandomNumberGenerator.GetBytes` and derives a 64-byte (512-bit) key via `Rfc2898DeriveBytes.Pbkdf2`
  with `HashAlgorithmName.SHA512` and `600_000` iterations (`PasswordHasher.cs:34`, `PasswordHasher.cs:35`).
  The constants are named `SaltSize = 32`, `HashSize = 64`, and `Iterations = 600_000`
  (`PasswordHasher.cs:15`, `PasswordHasher.cs:18`, `PasswordHasher.cs:24`), the iteration count tracking
  OWASP 2023 guidance for this primitive.
- **Verification picks the algorithm by salt length, not by a stored flag.** `VerifyPassword` branches on
  `salt.Length == LegacyHmacSaltSize` (`PasswordHasher.cs:52`), where `LegacyHmacSaltSize = 128`
  (`PasswordHasher.cs:27`): a 128-byte salt routes to the legacy HMAC-SHA512 recompute
  (`ComputeLegacyHash`, `PasswordHasher.cs:71`, which keys `HMACSHA512` with the stored salt), and any
  other length (the 32-byte current format) routes to PBKDF2 (`ComputePbkdf2Hash`, `PasswordHasher.cs:62`,
  using the stored hash length as the output length). The decision is encoded in the data, so the same
  call site verifies both eras transparently.
- **The legacy branch is load-bearing, not dead code.** It is the only path by which a pre-existing
  stored password still verifies. A naive refactor that "simplifies" `VerifyPassword` down to the PBKDF2
  call would compile cleanly, pass the new-format tests, deploy without error, and then silently reject
  every password written under the old scheme: a 128-byte salt fed to PBKDF2 produces a hash that can
  never equal the stored HMAC output. There is no exception and no startup failure to catch it; the only
  symptom is that legacy users cannot log in. That risk is exactly why this branch is recorded here.
- **Comparison is constant time.** Both paths compare the recomputed bytes to the stored hash with
  `CryptographicOperations.FixedTimeEquals` (`PasswordHasher.cs:58`), which always reads the full length
  so the verify time does not leak how many leading bytes matched.
- **Adopted by both apps' Identity flow, through a shared base.** Each app's `AuthenticationService` is now a
  sealed subclass of `AuthenticationServiceBase<TUser>`
  (`Source/Core/MMCA.Common.Application/Auth/AuthenticationServiceBase.cs:34`) that passes `IPasswordHasher`
  into the base constructor rather than calling the hasher itself. The login-time `VerifyPassword`
  (`AuthenticationServiceBase.cs:102`) and the registration-time `HashPassword` (`AuthenticationServiceBase.cs:150`)
  both live once in that base, so a single hoisted call site verifies and hashes for both apps. ADC's subclass
  declares the `IPasswordHasher` parameter and forwards it to the base
  (`MMCA.ADC/.../Identity.Application/Users/AuthenticationService.cs:25`, forwarded at `AuthenticationService.cs:30`);
  Store's subclass does the same (`MMCA.Store/.../Identity.Application/Users/AuthenticationService.cs:23`,
  forwarded at `AuthenticationService.cs:30`). A handful of use cases still inject `IPasswordHasher` directly
  rather than through the base: both apps' `ChangePasswordHandler`
  (`MMCA.ADC` and `MMCA.Store` `.../UseCases/ChangePassword/ChangePasswordHandler.cs:16`) verify the current
  password before hashing the new one, and both apps' `IdentityModuleDbSeeder`
  (`.../Persistence/DbContexts/Seeding/IdentityModuleDbSeeder.cs:18`) hash the seeded accounts' passwords.

## Rationale
- **One framework-owned primitive, not per-app crypto.** Putting the algorithm, work factor, salt size,
  and comparison in a single shared type means a future hardening (raising iterations, changing the KDF)
  is one edit that every consumer inherits, rather than a hunt across Identity modules.
- **Migrate on the user's own login, with no flag day.** Because the stored bytes themselves select the
  algorithm, legacy records keep working untouched, and there is no bulk migration job, no reset email
  blast, and no `IsLegacy` column to maintain. A record naturally moves to the current format the next
  time its owner sets or changes a password (which writes a 32-byte salt).
- **Slow-by-design KDF over a fast hash.** PBKDF2-HMAC-SHA512 at a high iteration count makes offline
  cracking of a leaked table expensive, which a single-pass HMAC (the legacy scheme) does not, so the new
  format is a genuine security upgrade rather than a cosmetic change.

## Trade-offs
- **The legacy branch is a permanent correctness dependency that looks deletable.** Its load-bearing role
  is invisible from the method body alone, so it is the single most refactor-fragile line in the hasher.
  The mitigation is this record plus the two focused regression tests in
  `Tests/Core/MMCA.Common.Infrastructure.Tests/Services/PasswordHasherTests.cs`
  (`PasswordHasherTests.cs:66` asserts a 128-byte-salt HMAC hash verifies true, `PasswordHasherTests.cs:78`
  asserts a wrong password against the legacy format verifies false), which fail loudly if the branch is
  dropped, alongside the current-format coverage at `PasswordHasherTests.cs:22` and `PasswordHasherTests.cs:51`.
- **Algorithm selection is heuristic, keyed on salt length.** It relies on the legacy and current salt
  sizes (128 vs 32 bytes) being distinct. They are, and a hostile or corrupt 128-byte salt at worst fails
  to verify rather than misauthenticates, but a future third format would have to pick a third salt length
  (or add an explicit version marker) rather than colliding on 128 or 32.
- **No automatic rehash on a successful legacy verify.** A legacy user who only ever logs in (never
  changes a password) stays on the old format indefinitely, because `VerifyPassword` does not re-emit a
  current-format hash on success. Opportunistic upgrade-on-login is possible but deliberately out of scope
  here; the current design accepts a long tail of legacy rows.
- **The work factor is a fixed compile-time constant.** `Iterations = 600_000` is not configurable per
  host, so raising it is a framework change and a release, not an appsetting. That keeps the security
  floor uniform across consumers at the cost of per-deployment tuning.

## Related
ADR-004 (cross-service JWT / JWKS authentication: the hasher gates credential verification that issues the
tokens that ADR-004 then validates across services),
ADR-005 (soft-delete vs erasure: the same Infrastructure layer's `EncryptedStringConverter` protects other
sensitive columns, the at-rest counterpart to hashing credentials),
ADR-029 (authentication brute-force protection: lockout and throttling wrap the same login path whose
final credential check is this hasher).
This ADR supersedes the one-line "Password hashing" note in `SECURITY.md:25` with a governance record;
the security model summary there stays as the reader-facing pointer.

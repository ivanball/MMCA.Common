# ADR-037: Field-Level Encryption at Rest (AES-256-GCM EF Converter)

## Status
Accepted (2026-07-06).

## Context
Transparent database encryption (TDE) protects the data files as a whole, but it decrypts
transparently for anyone who can query the database, so a leaked backup restored on a compromised
host, or a row read by an over-privileged connection, still yields plaintext. Some columns hold data
sensitive enough to warrant a second layer: the value should be ciphertext the moment it leaves the
application, so that only a holder of the application key (not merely a database reader) can recover
it.

This is a distinct control from the two sensitive-data mechanisms the framework already records.
Password hashing (ADR-032) is deliberately **one-way**: a credential is stored so it can be verified,
never recovered. Erasure (ADR-005) **removes or anonymizes** personal data on a data-subject request.
Neither covers the case of a field that must stay **retrievable in plaintext to the application** yet
be **unreadable in the database itself**: an at-rest, reversible, column-level confidentiality control.
ADR-005 already names this converter as the mechanism for erasure fields "that must remain retrievable"
(`ADRs/005-soft-delete-vs-erasure.md:17`), and `SECURITY.md:26` lists it in the security model, so the
capability is referenced across the docs but was never recorded as a decision.

The framework ships the plumbing for this, an EF Core value converter that encrypts and decrypts string
columns transparently, and it is covered by unit tests. It is **not yet adopted**: no entity
configuration in any of the four repositories wires it, so zero production columns are encrypted today.
This ADR records the decision and the seam while being explicit about that posture, the same
"shipped in the framework, tested, but latent until the first adoption" honesty ADR-018 records for
polyglot persistence.

## Decision
Provide a single framework-owned EF Core value converter that transparently encrypts string columns at
rest with authenticated encryption, applied per property in an entity configuration.

1. **A sealed EF value converter.** `EncryptedStringConverter`
   (`Source/Core/MMCA.Common.Infrastructure/Persistence/Encryption/EncryptedStringConverter.cs:28`) is a
   `ValueConverter<string, string>` in the `MMCA.Common.Infrastructure.Persistence.Encryption` namespace
   (`EncryptedStringConverter.cs:5`). It encrypts on write and decrypts on read, so application and domain
   code keep an ordinary `string` property and never see ciphertext. It is applied per property:
   `builder.Property(e => e.Email).HasConversion(new EncryptedStringConverter(encryptionKey))`
   (`EncryptedStringConverter.cs:12`, `:15`).

2. **AES-256-GCM authenticated encryption.** Both directions use `AesGcm`
   (`EncryptedStringConverter.cs:70`, `:99`), which provides confidentiality **and** integrity. The key
   must be exactly 32 bytes (256 bits): the constructor null-checks it
   (`ArgumentNullException.ThrowIfNull`, `EncryptedStringConverter.cs:45`) and throws `ArgumentException`
   on any other length (`EncryptedStringConverter.cs:46`, `:48`). `GenerateKey()` produces a
   cryptographically random 32-byte key via `RandomNumberGenerator.GetBytes(32)`
   (`EncryptedStringConverter.cs:58`). The nonce and tag sizes are fixed constants: `NonceSize = 12`
   (96 bits, NIST recommended, `EncryptedStringConverter.cs:31`) and `TagSize = 16`
   (128 bits, `EncryptedStringConverter.cs:34`).

3. **Self-describing storage layout, Base64 in a string column.** Encrypt writes UTF-8 plaintext bytes
   (`EncryptedStringConverter.cs:65`), draws a fresh random nonce (`EncryptedStringConverter.cs:66`),
   runs `AesGcm.Encrypt` (`EncryptedStringConverter.cs:71`), then concatenates
   `[nonce (12)] [ciphertext (N)] [tag (16)]` (`EncryptedStringConverter.cs:73`, `:74`) and Base64-encodes
   the result (`EncryptedStringConverter.cs:79`). Decrypt reverses it: `FromBase64String`
   (`EncryptedStringConverter.cs:87`), a length guard that throws `CryptographicException` when the input
   is shorter than nonce plus tag (`EncryptedStringConverter.cs:89`, `:90`), spans that slice out the
   nonce, ciphertext, and tag (`EncryptedStringConverter.cs:92`, `:94`, `:95`), `AesGcm.Decrypt` which
   validates the tag while decrypting (`EncryptedStringConverter.cs:100`), and a UTF-8 decode
   (`EncryptedStringConverter.cs:102`). The layout is transparent to application code.

4. **Ciphertext is non-deterministic.** A fresh random nonce per encryption
   (`EncryptedStringConverter.cs:66`) means the same plaintext encrypts to different ciphertext on every
   write (proven at `Tests/Core/MMCA.Common.Infrastructure.Tests/Persistence/EncryptedStringConverterTests.cs:38`,
   and distinct plaintexts differ at `EncryptedStringConverterTests.cs:24`). The consequence is deliberate:
   an encrypted column cannot be equality-filtered, index-seeked, sorted, or joined on in the database.

5. **Empty and null values pass through unencrypted.** Both directions short-circuit on a null-or-empty
   string (`EncryptedStringConverter.cs:62`, `:84`), so a NULL or empty column stays as-is rather than
   becoming ciphertext (tests at `EncryptedStringConverterTests.cs:82` and `:95`).

6. **Key management is the consumer's responsibility, supplied as a constructor argument.** The converter
   takes a raw `byte[]` key on construction (`EncryptedStringConverter.cs:40`); there is no DI
   registration, no options type, and no key-provider abstraction in the Infrastructure layer (a grep of
   `MMCA.Common.Infrastructure` for encryption options or a key-provider interface finds only the converter
   itself). The adopting entity configuration passes the key in. The XML documentation directs consumers to
   store that key in Azure Key Vault, user-secrets, or environment variables, never hardcoded
   (`EncryptedStringConverter.cs:19`, `:20`, `:21`).

7. **Unit-tested but not yet adopted.** `EncryptedStringConverterTests`
   (`Tests/Core/MMCA.Common.Infrastructure.Tests/Persistence/EncryptedStringConverterTests.cs:6`) covers a
   plaintext round-trip (`EncryptedStringConverterTests.cs:10`), a Unicode round-trip
   (`EncryptedStringConverterTests.cs:129`), non-deterministic output (`EncryptedStringConverterTests.cs:24`,
   `:38`), the 32-byte key generation (`EncryptedStringConverterTests.cs:52`, `:61`), the invalid-length and
   null-key guards (`EncryptedStringConverterTests.cs:71`, `:123`), the empty-string passthrough
   (`EncryptedStringConverterTests.cs:82`, `:95`), and the too-short-ciphertext `CryptographicException`
   (`EncryptedStringConverterTests.cs:108`). Adoption, however, is zero: a ripgrep across all four
   repositories (`MMCA.Common`, `MMCA.Store`, `MMCA.ADC`, `MMCA.Helpdesk`) finds
   `new EncryptedStringConverter(` only in the converter's own XML-doc example
   (`EncryptedStringConverter.cs:15`) and in that test file. No `*Configuration.cs` in any repo calls
   `.HasConversion(new EncryptedStringConverter(...))`, and Store, ADC, and Helpdesk contain no reference to
   the type at all. The encrypt/decrypt path is exercised by tests, not by any live column.

## Rationale
- **Authenticated, not merely confidential.** AES-GCM binds a 128-bit tag to the ciphertext
  (`EncryptedStringConverter.cs:34`, `:71`), so a tampered or truncated value fails to decrypt via the tag
  check in `AesGcm.Decrypt` (`EncryptedStringConverter.cs:100`) rather than silently returning corrupted
  plaintext, and a value too short to even hold a nonce and tag is rejected up front
  (`EncryptedStringConverter.cs:89`). At-rest integrity comes for free with confidentiality.
- **One framework-owned primitive.** As with password hashing (ADR-032), the algorithm, key size, nonce
  size, and storage layout are decided once in a single shared type, so a future hardening is one edit that
  every eventual adopter inherits rather than per-app crypto scattered across modules.
- **Non-determinism is the right confidentiality default.** A random nonce per write
  (`EncryptedStringConverter.cs:66`) defeats equality and frequency analysis over the ciphertext, which a
  deterministic scheme would leak; the cost is queryability, which is the correct trade for a genuinely
  sensitive column that the application reads by primary key rather than by the encrypted value.
- **Transparent at the EF boundary.** Because the conversion lives on the property mapping
  (`EncryptedStringConverter.cs:12`), entities keep `string` properties and no handler, DTO, or domain code
  changes when a column becomes encrypted.

## Trade-offs
- **Latent today, proven by tests rather than production.** The plumbing is complete and unit-tested, but
  no entity configuration wires it, so the encrypt/decrypt round-trip, the tag-validated integrity path, and
  the key-length guard are exercised only by `EncryptedStringConverterTests` and not by any deployed column.
  ADR-005 names this converter as the mechanism for erasure fields that must remain retrievable
  (`ADRs/005-soft-delete-vs-erasure.md:17`), but that pairing is available, not yet applied. This is the same
  shipped-but-unadopted posture ADR-018 records for polyglot persistence.
- **Encrypted columns are not queryable.** The random nonce (`EncryptedStringConverter.cs:66`) makes
  ciphertext non-deterministic, so there is no equality filter, index seek, sort, or join on an encrypted
  column. A field that must be both encrypted and looked up needs a separate deterministic scheme or a blind
  index, neither of which this converter provides.
- **Key management is entirely the consumer's, with no rotation story.** The converter takes a raw key
  (`EncryptedStringConverter.cs:40`) and the stored layout is nonce plus ciphertext plus tag only, carrying
  no key identifier or version (`EncryptedStringConverter.cs:73`). Rotating the key therefore requires bulk
  re-encryption, there is no built-in decrypt-with-old / encrypt-with-new path, and losing the key makes the
  data permanently unrecoverable. Envelope encryption and key versioning are out of scope for this converter.
- **Per-property wiring, not a global switch.** Encryption is opted into one `HasConversion` call at a time
  in each entity configuration (`EncryptedStringConverter.cs:12`), so a column that should be encrypted but is
  never wired silently stays plaintext, the same audit-the-inventory caveat as ADR-005.
- **Storage and CPU overhead.** Every value grows by 28 bytes (12-byte nonce plus 16-byte tag,
  `EncryptedStringConverter.cs:31`, `:34`) before Base64 inflation, and every read and write performs an
  AES-GCM operation.
- **Test coverage stops at the too-short guard.** Integrity rests on AES-GCM's tag (a property of the
  primitive), and the only malformed-input regression test is the short-ciphertext case
  (`EncryptedStringConverterTests.cs:108`); there is no explicit bit-flip-tamper or wrong-key test, so a
  refactor that weakened tag validation would not be caught by the current suite.

## Related
ADR-005 (soft-delete vs erasure: the other sensitive-data control, which names this converter as the
mechanism for erasure fields that must stay retrievable, `ADRs/005-soft-delete-vs-erasure.md:17`), ADR-032
(password hashing: the one-way credential counterpart in the same Infrastructure layer, which already calls
this converter "the at-rest counterpart to hashing credentials"), ADR-018 (polyglot persistence: the
shipped, tested, but unadopted precedent this record mirrors). This ADR backs the one-line "Field encryption"
entry in the security model (`SECURITY.md:26`), which stays as the reader-facing pointer.

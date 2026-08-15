using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MMCA.Common.Infrastructure.Persistence.Encryption;

/// <summary>
/// EF Core value converter that encrypts string values before writing to the database
/// and decrypts them when reading. Uses AES-256-GCM for authenticated encryption,
/// providing both confidentiality and integrity protection.
/// <para>
/// <b>Usage:</b> Apply to individual properties in EF entity configurations:
/// <code>
/// builder.Property(e => e.SocialSecurityNumber)
///     .HasConversion(new EncryptedStringConverter(encryptionKey));
/// </code>
/// </para>
/// <para>
/// <b>Constraint: the ciphertext is non-deterministic.</b> Every write uses a fresh random nonce,
/// so the same plaintext encrypts to a different column value each time. That is the correct
/// property for confidentiality, and it means the column cannot support:
/// <list type="bullet">
///   <item>equality or range predicates (<c>Where(u =&gt; u.Email == value)</c> translates to a
///   comparison against a ciphertext that will never match, returning no rows silently);</item>
///   <item>unique indexes or any other constraint over the stored value;</item>
///   <item>server-side sorting or grouping.</item>
/// </list>
/// Only apply it to properties that are read back as part of an entity and never queried by value.
/// A lookup key that must stay searchable needs a separate deterministic surface, such as a
/// keyed hash stored alongside the encrypted column.
/// </para>
/// <para>
/// <b>Key management:</b> The encryption key should be stored securely (e.g., Azure Key Vault,
/// user-secrets, or environment variables): never hardcoded. Every key must be exactly 32 bytes
/// (256 bits) for AES-256. Use <see cref="GenerateKey"/> to create a new key.
/// </para>
/// <para>
/// <b>Storage format:</b> The encrypted value is stored as a Base64 string containing the key
/// version (1 byte) + nonce (12 bytes) + ciphertext + authentication tag (16 bytes). This is
/// transparent to application code. The version byte is also passed to AES-GCM as associated
/// data, so it is covered by the authentication tag: rewriting the version byte of a stored
/// value fails decryption instead of silently selecting a different key.
/// </para>
/// <para>
/// <b>Key ring and rotation:</b> A converter can be constructed over a whole ring of versioned
/// keys, with one of them nominated as current. Writes always use the current version; reads
/// resolve their key from the version byte in the stored value itself. Rotation is therefore a
/// four-step, zero-downtime operation:
/// <list type="number">
///   <item>add the new key to the ring under a new version and make that version current, keeping
///   the old versions registered;</item>
///   <item>deploy: new writes carry the new version byte, existing rows keep decrypting under
///   their original version;</item>
///   <item>re-encrypt the existing rows in the background at your own pace (load an entity and
///   save it, which rewrites the column under the current version);</item>
///   <item>once no row carries the old version, drop that entry from the ring.</item>
/// </list>
/// A value whose version is not registered fails with a <see cref="CryptographicException"/>
/// rather than decrypting to garbage.
/// </para>
/// <para>
/// <b>Stateless and context-free by design:</b> the converter holds only the frozen key ring
/// captured at construction, and version resolution is data-driven from the stored envelope.
/// It never consults the <c>DbContext</c>, the current user, or any ambient scope, because a
/// value converter runs inside the provider's materialization path and has no access to them.
/// That also makes per-tenant or per-request key selection deliberately out of scope here: those
/// need a <c>SaveChanges</c> interceptor or application-layer encryption above EF Core, where the
/// request context is still reachable.
/// </para>
/// </summary>
public sealed class EncryptedStringConverter : ValueConverter<string, string>
{
    /// <summary>Key version prefix size in bytes.</summary>
    private const int VersionSize = 1;

    /// <summary>AES-GCM nonce size in bytes (96 bits, NIST recommended).</summary>
    private const int NonceSize = 12;

    /// <summary>AES-GCM authentication tag size in bytes (128 bits).</summary>
    private const int TagSize = 16;

    /// <summary>Required key size in bytes (256 bits).</summary>
    private const int KeySize = 32;

    /// <summary>Key version assigned to the single-key constructor.</summary>
    private const byte DefaultKeyVersion = 1;

    /// <summary>
    /// Initializes a new instance of the <see cref="EncryptedStringConverter"/> class over a
    /// single key, registered as version <c>1</c> and used for all writes.
    /// </summary>
    /// <param name="encryptionKey">A 32-byte (256-bit) AES encryption key.</param>
    public EncryptedStringConverter(byte[] encryptionKey)
        : this(CreateSingleKeyRing(encryptionKey), DefaultKeyVersion)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EncryptedStringConverter"/> class over a ring
    /// of versioned keys. Writes use <paramref name="currentKeyVersion"/>; reads resolve their key
    /// from the version byte stored with the value, so older versions stay readable while they
    /// remain in the ring.
    /// </summary>
    /// <param name="keyRing">Key version to 32-byte (256-bit) AES key. Copied defensively, so
    /// later mutation of the supplied dictionary does not affect this converter.</param>
    /// <param name="currentKeyVersion">The version used to encrypt new values. Must be present
    /// in <paramref name="keyRing"/>.</param>
    public EncryptedStringConverter(IReadOnlyDictionary<byte, byte[]> keyRing, byte currentKeyVersion)
        : this(ValidateAndFreeze(keyRing, currentKeyVersion), currentKeyVersion)
    {
    }

    private EncryptedStringConverter(FrozenDictionary<byte, byte[]> keyRing, byte currentKeyVersion)
        : base(
            plaintext => Encrypt(plaintext, keyRing, currentKeyVersion),
            ciphertext => Decrypt(ciphertext, keyRing))
    {
    }

    /// <summary>
    /// Generates a cryptographically random 256-bit AES key suitable for use with this converter.
    /// </summary>
    /// <returns>A 32-byte random key.</returns>
    public static byte[] GenerateKey() => RandomNumberGenerator.GetBytes(KeySize);

    private static Dictionary<byte, byte[]> CreateSingleKeyRing(byte[] encryptionKey)
    {
        ArgumentNullException.ThrowIfNull(encryptionKey);
        if (encryptionKey.Length != KeySize)
        {
            throw new ArgumentException(
                $"Encryption key must be exactly 32 bytes (256 bits). Received {encryptionKey.Length} bytes.",
                nameof(encryptionKey));
        }

        return new Dictionary<byte, byte[]>(1) { [DefaultKeyVersion] = encryptionKey };
    }

    /// <summary>
    /// Validates a caller-supplied key ring and returns an immutable copy of it. The copy is what
    /// the conversion delegates capture, so mutating the original dictionary afterwards cannot
    /// change which keys this converter uses. Key material itself is kept by reference, matching
    /// the single-key constructor.
    /// </summary>
    private static FrozenDictionary<byte, byte[]> ValidateAndFreeze(
        IReadOnlyDictionary<byte, byte[]> keyRing,
        byte currentKeyVersion)
    {
        ArgumentNullException.ThrowIfNull(keyRing);

        if (keyRing.Count == 0)
        {
            throw new ArgumentException("Key ring must contain at least one key.", nameof(keyRing));
        }

        foreach (var entry in keyRing)
        {
            if (entry.Value is null)
            {
                throw new ArgumentException(
                    $"Key ring entry for version {entry.Key} is null. Every key must be exactly 32 bytes (256 bits).",
                    nameof(keyRing));
            }

            if (entry.Value.Length != KeySize)
            {
                throw new ArgumentException(
                    $"Key ring entry for version {entry.Key} must be exactly 32 bytes (256 bits). Received {entry.Value.Length} bytes.",
                    nameof(keyRing));
            }
        }

        if (!keyRing.ContainsKey(currentKeyVersion))
        {
            throw new ArgumentException(
                $"Key ring does not contain the current key version {currentKeyVersion}.",
                nameof(keyRing));
        }

        return keyRing.ToFrozenDictionary();
    }

    private static string Encrypt(string plaintext, FrozenDictionary<byte, byte[]> keyRing, byte keyVersion)
    {
        if (string.IsNullOrEmpty(plaintext))
            return plaintext;

        // Validated at construction, so the current version is always present.
        var key = keyRing[keyVersion];

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        // The version byte travels as associated data, so the tag authenticates it.
        ReadOnlySpan<byte> associatedData = [keyVersion];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, associatedData);

        // Format: [key version (1)] [nonce (12)] [ciphertext (N)] [tag (16)]
        var result = new byte[VersionSize + NonceSize + ciphertext.Length + TagSize];
        result[0] = keyVersion;
        nonce.CopyTo(result, VersionSize);
        ciphertext.CopyTo(result, VersionSize + NonceSize);
        tag.CopyTo(result, VersionSize + NonceSize + ciphertext.Length);

        return Convert.ToBase64String(result);
    }

    private static string Decrypt(string encoded, FrozenDictionary<byte, byte[]> keyRing)
    {
        if (string.IsNullOrEmpty(encoded))
            return encoded;

        var combined = Convert.FromBase64String(encoded);

        if (combined.Length < VersionSize + NonceSize + TagSize)
            throw new CryptographicException("Encrypted value is too short to contain a valid key version, nonce and tag.");

        var keyVersion = combined[0];
        if (!keyRing.TryGetValue(keyVersion, out var key))
            throw new CryptographicException($"No key registered for key version {keyVersion}.");

        var nonce = combined.AsSpan(VersionSize, NonceSize);
        var ciphertextLength = combined.Length - VersionSize - NonceSize - TagSize;
        var ciphertext = combined.AsSpan(VersionSize + NonceSize, ciphertextLength);
        var tag = combined.AsSpan(VersionSize + NonceSize + ciphertextLength, TagSize);
        var associatedData = combined.AsSpan(0, VersionSize);

        var plaintext = new byte[ciphertextLength];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);

        return Encoding.UTF8.GetString(plaintext);
    }
}

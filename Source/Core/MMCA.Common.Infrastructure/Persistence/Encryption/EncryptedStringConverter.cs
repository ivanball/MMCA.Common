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
/// builder.Property(e => e.Email)
///     .HasConversion(new EncryptedStringConverter(encryptionKey));
/// </code>
/// </para>
/// <para>
/// <b>Key management:</b> The encryption key should be stored securely (e.g., Azure Key Vault,
/// user-secrets, or environment variables) — never hardcoded. The key must be exactly 32 bytes
/// (256 bits) for AES-256. Use <see cref="GenerateKey"/> to create a new key.
/// </para>
/// <para>
/// <b>Storage format:</b> The encrypted value is stored as a Base64 string containing the nonce
/// (12 bytes) + ciphertext + authentication tag (16 bytes). This is transparent to application code.
/// </para>
/// </summary>
public sealed class EncryptedStringConverter : ValueConverter<string, string>
{
    /// <summary>AES-GCM nonce size in bytes (96 bits, NIST recommended).</summary>
    private const int NonceSize = 12;

    /// <summary>AES-GCM authentication tag size in bytes (128 bits).</summary>
    private const int TagSize = 16;

    /// <summary>
    /// Initializes a new instance of the <see cref="EncryptedStringConverter"/> class.
    /// </summary>
    /// <param name="encryptionKey">A 32-byte (256-bit) AES encryption key.</param>
    public EncryptedStringConverter(byte[] encryptionKey)
        : base(
            plaintext => Encrypt(plaintext, encryptionKey),
            ciphertext => Decrypt(ciphertext, encryptionKey))
    {
        ArgumentNullException.ThrowIfNull(encryptionKey);
        if (encryptionKey.Length != 32)
        {
            throw new ArgumentException(
                $"Encryption key must be exactly 32 bytes (256 bits). Received {encryptionKey.Length} bytes.",
                nameof(encryptionKey));
        }
    }

    /// <summary>
    /// Generates a cryptographically random 256-bit AES key suitable for use with this converter.
    /// </summary>
    /// <returns>A 32-byte random key.</returns>
    public static byte[] GenerateKey() => RandomNumberGenerator.GetBytes(32);

    private static string Encrypt(string plaintext, byte[] key)
    {
        if (string.IsNullOrEmpty(plaintext))
            return plaintext;

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Format: [nonce (12)] [ciphertext (N)] [tag (16)]
        var result = new byte[NonceSize + ciphertext.Length + TagSize];
        nonce.CopyTo(result, 0);
        ciphertext.CopyTo(result, NonceSize);
        tag.CopyTo(result, NonceSize + ciphertext.Length);

        return Convert.ToBase64String(result);
    }

    private static string Decrypt(string encoded, byte[] key)
    {
        if (string.IsNullOrEmpty(encoded))
            return encoded;

        var combined = Convert.FromBase64String(encoded);

        if (combined.Length < NonceSize + TagSize)
            throw new CryptographicException("Encrypted value is too short to contain a valid nonce and tag.");

        var nonce = combined.AsSpan(0, NonceSize);
        var ciphertextLength = combined.Length - NonceSize - TagSize;
        var ciphertext = combined.AsSpan(NonceSize, ciphertextLength);
        var tag = combined.AsSpan(NonceSize + ciphertextLength, TagSize);

        var plaintext = new byte[ciphertextLength];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }
}

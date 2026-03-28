using System.Security.Cryptography;
using System.Text;
using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// Hashes and verifies passwords using PBKDF2-HMAC-SHA512 with 600,000 iterations
/// (OWASP-recommended). Backward-compatible with legacy HMAC-SHA512 hashes: detects
/// the algorithm from the salt length (32 bytes = PBKDF2, 128 bytes = legacy HMAC-SHA512).
/// </summary>
public sealed class PasswordHasher : IPasswordHasher
{
    /// <summary>PBKDF2 salt size in bytes (256 bits).</summary>
    private const int SaltSize = 32;

    /// <summary>PBKDF2 hash output size in bytes (512 bits).</summary>
    private const int HashSize = 64;

    /// <summary>
    /// OWASP-recommended iteration count for PBKDF2-HMAC-SHA512 (2023 guidance).
    /// High iteration count makes brute-force attacks computationally expensive.
    /// </summary>
    private const int Iterations = 600_000;

    /// <summary>Legacy HMAC-SHA512 salt size (the HMAC key length).</summary>
    private const int LegacyHmacSaltSize = 128;

    /// <inheritdoc />
    public (byte[] Hash, byte[] Salt) HashPassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Iterations,
            HashAlgorithmName.SHA512,
            HashSize);

        return (hash, salt);
    }

    /// <inheritdoc />
    public bool VerifyPassword(string password, byte[] hash, byte[] salt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentNullException.ThrowIfNull(salt);

        var computedHash = salt.Length == LegacyHmacSaltSize
            ? ComputeLegacyHash(password, salt)
            : ComputePbkdf2Hash(password, salt, hash.Length);

        // FixedTimeEquals prevents timing side-channel attacks by always comparing
        // the full length regardless of where the first difference occurs.
        return CryptographicOperations.FixedTimeEquals(computedHash, hash);
    }

    /// <summary>Computes a PBKDF2-HMAC-SHA512 hash for the current algorithm.</summary>
    private static byte[] ComputePbkdf2Hash(string password, byte[] salt, int outputLength) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Iterations,
            HashAlgorithmName.SHA512,
            outputLength);

    /// <summary>Computes a legacy HMAC-SHA512 hash for backward compatibility with existing passwords.</summary>
    private static byte[] ComputeLegacyHash(string password, byte[] salt)
    {
        using var hmac = new HMACSHA512(salt);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(password));
    }
}

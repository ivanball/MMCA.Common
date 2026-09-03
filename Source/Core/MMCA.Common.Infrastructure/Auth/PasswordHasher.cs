using System.Security.Cryptography;
using System.Text;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;

namespace MMCA.Common.Infrastructure.Auth;

/// <summary>
/// Hashes and verifies passwords using PBKDF2-HMAC-SHA512 with 600,000 iterations
/// (OWASP-recommended). PBKDF2 is the only supported algorithm: every stored hash is
/// derived and verified through it.
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

        var computedHash = ComputePbkdf2Hash(password, salt, hash.Length);

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
}

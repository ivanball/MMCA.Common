using System.Security.Cryptography;
using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// Hashes and verifies passwords using HMACSHA512. Each password gets a unique random salt
/// (the HMAC key), which is stored alongside the hash.
/// </summary>
public sealed class PasswordHasher : IPasswordHasher
{
    /// <inheritdoc />
    public (byte[] Hash, byte[] Salt) HashPassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        // HMACSHA512() without arguments generates a cryptographically random key (= salt).
        using var hmac = new HMACSHA512();
        var salt = hmac.Key;
        var hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(password));
        return (hash, salt);
    }

    /// <inheritdoc />
    public bool VerifyPassword(string password, byte[] hash, byte[] salt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentNullException.ThrowIfNull(salt);

        using var hmac = new HMACSHA512(salt);
        var computedHash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(password));

        // FixedTimeEquals prevents timing side-channel attacks by always comparing
        // the full length regardless of where the first difference occurs.
        return CryptographicOperations.FixedTimeEquals(computedHash, hash);
    }
}

namespace MMCA.Common.Application.Interfaces.Infrastructure;

/// <summary>
/// Hashes and verifies passwords using a salted hash algorithm.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>Hashes a plaintext password, producing a hash and a cryptographic salt.</summary>
    /// <param name="password">The plaintext password to hash.</param>
    /// <returns>A tuple of the password hash and the salt used.</returns>
    (byte[] Hash, byte[] Salt) HashPassword(string password);

    /// <summary>Verifies a plaintext password against a stored hash and salt.</summary>
    /// <param name="password">The plaintext password to verify.</param>
    /// <param name="hash">The stored password hash.</param>
    /// <param name="salt">The stored salt.</param>
    /// <returns><see langword="true"/> if the password matches the hash.</returns>
    bool VerifyPassword(string password, byte[] hash, byte[] salt);
}

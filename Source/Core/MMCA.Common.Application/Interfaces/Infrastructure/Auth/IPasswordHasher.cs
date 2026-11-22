namespace MMCA.Common.Application.Interfaces.Infrastructure.Auth;

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

    /// <summary>
    /// Whether a stored hash that has just verified was derived with parameters other than the ones
    /// <see cref="HashPassword"/> uses now (an older format or work factor), so the caller should
    /// re-derive it from the plaintext it is holding. The default answers <see langword="false"/>,
    /// which keeps a custom hasher compiling and never triggers a rehash.
    /// </summary>
    /// <param name="hash">The stored password hash.</param>
    /// <returns><see langword="true"/> if the hash should be replaced by a fresh <see cref="HashPassword"/> result.</returns>
    bool NeedsRehash(byte[] hash) => false;

    /// <summary>
    /// Runs one throwaway key derivation of <paramref name="password"/> that costs what verifying the
    /// most expensive record this hasher can have stored costs, for a sign-in branch that never reaches
    /// a real verification (an unknown address, an account with no password). A hasher that still
    /// verifies records derived at an older, higher work factor burns at that factor, so those branches
    /// cannot be told apart by timing. The default derives one <see cref="HashPassword"/> and discards
    /// it, which is right for a hasher whose every stored record uses its current parameters.
    /// </summary>
    /// <param name="password">The submitted plaintext password; never null or whitespace.</param>
    void BurnVerificationCost(string password) => _ = HashPassword(password);
}

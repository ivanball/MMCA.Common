using System.Security.Cryptography;
using System.Text;

namespace MMCA.Common.Domain.Auth.MachineClients;

/// <summary>
/// The machine-client secret format (ADR-140): 32 random bytes, handed out once base64url-encoded,
/// stored only as their SHA-256 hash, and compared in constant time.
/// </summary>
/// <remarks>
/// A plain SHA-256 rather than a password hash on purpose: the secret is 256 bits of
/// <see cref="RandomNumberGenerator"/> output, so there is nothing for a slow hash to protect against,
/// and the token endpoint stays cheap under a burst of requests.
/// </remarks>
public static class MachineClientSecret
{
    /// <summary>The number of random bytes in a secret.</summary>
    public const int SecretByteLength = 32;

    /// <summary>The length of a stored hash (SHA-256).</summary>
    public const int HashByteLength = 32;

    /// <summary>Generates a new secret.</summary>
    /// <returns>The base64url-encoded secret, to be shown once.</returns>
    public static string Generate() => Base64UrlEncode(RandomNumberGenerator.GetBytes(SecretByteLength));

    /// <summary>Hashes a presented or freshly generated secret for storage or comparison.</summary>
    /// <param name="secret">The secret as the client presents it.</param>
    /// <returns>The SHA-256 hash of the secret's UTF-8 bytes.</returns>
    public static byte[] Hash(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }

    /// <summary>Compares two hashes in constant time.</summary>
    /// <param name="presented">The hash of the presented secret.</param>
    /// <param name="stored">A stored hash, or <see langword="null"/> when that slot is empty.</param>
    /// <returns><see langword="true"/> when the stored hash exists and matches.</returns>
    public static bool Matches(ReadOnlySpan<byte> presented, byte[]? stored) =>
        stored is { Length: HashByteLength } && CryptographicOperations.FixedTimeEquals(presented, stored);

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

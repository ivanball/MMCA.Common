using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;

namespace MMCA.Common.Infrastructure.Auth;

/// <summary>
/// Hashes and verifies passwords using PBKDF2-HMAC-SHA512. New hashes are derived at 210,000
/// iterations, the OWASP Password Storage Cheat Sheet figure for PBKDF2-HMAC-SHA512 (600,000 is its
/// figure for PBKDF2-HMAC-SHA256). PBKDF2 is the only supported algorithm: every stored hash is
/// derived and verified through it.
/// <para>
/// Every hash this type writes carries its own work factor:
/// <c>[0x01 version][4-byte big-endian iterations][64-byte PBKDF2 output]</c>, 69 bytes in all. A
/// bare 64-byte hash is the earlier format, which carried no header and was always derived at
/// 600,000 iterations. Both shapes verify; <see cref="NeedsRehash"/> reports the legacy shape and any
/// stored count other than the current one, so the sign-in flow can upgrade the record while it
/// holds the plaintext.
/// </para>
/// </summary>
public sealed class PasswordHasher : IPasswordHasher
{
    /// <summary>PBKDF2 salt size in bytes (256 bits).</summary>
    private const int SaltSize = 32;

    /// <summary>PBKDF2 hash output size in bytes (512 bits).</summary>
    private const int HashSize = 64;

    /// <summary>
    /// Iteration count for new hashes: OWASP's PBKDF2-HMAC-SHA512 recommendation. Stored in each hash,
    /// so raising it later re-derives existing records on their next sign-in rather than breaking them.
    /// </summary>
    private const int Iterations = 210_000;

    /// <summary>The iteration count every header-less (64-byte) hash was derived with.</summary>
    private const int LegacyIterations = 600_000;

    /// <summary>The format version byte that leads every versioned hash.</summary>
    private const byte FormatVersion = 0x01;

    /// <summary>Header size: one version byte plus a 4-byte big-endian iteration count.</summary>
    private const int HeaderSize = 5;

    /// <summary>Total size of a versioned hash (header plus PBKDF2 output).</summary>
    private const int VersionedHashSize = HeaderSize + HashSize;

    /// <summary>
    /// Lowest stored iteration count accepted. A row claiming fewer was not written by this hasher,
    /// and honoring it would let a tampered row lower the cost of guessing its password.
    /// </summary>
    private const int MinIterations = 100_000;

    /// <summary>
    /// Highest stored iteration count accepted. Bounds the work one verification can be made to do,
    /// so a tampered row cannot turn a sign-in into a CPU denial of service.
    /// </summary>
    private const int MaxIterations = 2_000_000;

    /// <inheritdoc />
    public (byte[] Hash, byte[] Salt) HashPassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var derived = ComputePbkdf2Hash(password, salt, Iterations);

        var hash = new byte[VersionedHashSize];
        hash[0] = FormatVersion;
        BinaryPrimitives.WriteInt32BigEndian(hash.AsSpan(1, 4), Iterations);
        derived.CopyTo(hash.AsSpan(HeaderSize));

        return (hash, salt);
    }

    /// <inheritdoc />
    public bool VerifyPassword(string password, byte[] hash, byte[] salt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentNullException.ThrowIfNull(salt);

        // SECURITY: only canonical credential material can verify. The output length used to be
        // taken from the stored hash, so a row holding an empty hash and salt (the shape an
        // external-OAuth account carries, ADR-036) derived an empty hash and compared two empty
        // spans, which FixedTimeEquals answers true for: any password verified against it. A row
        // whose material is not one of the two shapes this hasher ever produced (a SaltSize salt
        // with either a legacy HashSize hash or a VersionedHashSize hash whose header is valid)
        // verifies nothing.
        if (salt.Length != SaltSize || !TryReadIterations(hash, out var iterations))
        {
            return false;
        }

        var expected = hash.AsSpan(hash.Length - HashSize);
        var computedHash = ComputePbkdf2Hash(password, salt, iterations);

        // FixedTimeEquals prevents timing side-channel attacks by always comparing
        // the full length regardless of where the first difference occurs.
        return CryptographicOperations.FixedTimeEquals(computedHash, expected);
    }

    /// <inheritdoc />
    /// <remarks>
    /// True for a legacy 64-byte hash and for a versioned hash whose stored count differs from the
    /// current one. False for the current shape and for material this hasher does not recognize
    /// (such material never verifies, so there is no sign-in to upgrade it on).
    /// </remarks>
    public bool NeedsRehash(byte[] hash)
    {
        ArgumentNullException.ThrowIfNull(hash);

        return TryReadIterations(hash, out var iterations)
            && (hash.Length == HashSize || iterations != Iterations);
    }

    /// <summary>
    /// Reads the work factor a stored hash was derived with, accepting exactly the two shapes this
    /// hasher writes: a header-less legacy hash (implicitly <see cref="LegacyIterations"/>) or a
    /// versioned hash whose version byte matches and whose count lies within the accepted bounds.
    /// </summary>
    private static bool TryReadIterations(byte[] hash, out int iterations)
    {
        if (hash.Length == HashSize)
        {
            iterations = LegacyIterations;
            return true;
        }

        if (hash.Length == VersionedHashSize && hash[0] == FormatVersion)
        {
            iterations = BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(1, 4));
            return iterations is >= MinIterations and <= MaxIterations;
        }

        iterations = 0;
        return false;
    }

    /// <summary>Computes a PBKDF2-HMAC-SHA512 digest at the given work factor.</summary>
    private static byte[] ComputePbkdf2Hash(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA512,
            HashSize);
}

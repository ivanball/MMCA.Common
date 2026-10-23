using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using MMCA.Common.Infrastructure.Auth;

namespace MMCA.Common.Infrastructure.Tests.Auth;

/// <summary>
/// Executable security invariants for <see cref="PasswordHasher"/>. The behavioural round-trips live in
/// <see cref="PasswordHasherTests"/> and stay green even if every cryptographic parameter is weakened,
/// because a hasher that hashes and verifies with the same weak parameters still round-trips. These tests
/// pin the parameters themselves: known-answer tests recompute the digest independently (PBKDF2-HMAC-SHA512,
/// 32-byte salt, 64-byte output; 210,000 iterations for new hashes, the OWASP figure for SHA-512, and
/// 600,000 for the header-less legacy shape), a negative known-answer test proves the iteration count
/// actually participates in verification, the versioned header is checked byte for byte and its bounds
/// are probed, and reflection pins the private constants so lowering one fails the build instead of
/// silently shipping.
/// </summary>
public sealed class PasswordHasherSecurityTests
{
    private const int PinnedIterations = 210_000;
    private const int PinnedLegacyIterations = 600_000;
    private const int PinnedSaltSize = 32;
    private const int PinnedHashSize = 64;
    private const int PinnedVersionedHashSize = 69;
    private const byte PinnedFormatVersion = 0x01;

    private readonly PasswordHasher _sut = new();

    // ── Known-answer tests (the parameters, not the round-trip) ──
    [Fact]
    public void VerifyPassword_AcceptsALegacy64ByteDigestComputedAt600000Iterations()
    {
        const string password = "KnownAnswerPassword123";
        var salt = DeterministicSalt();
        var expected = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            PinnedLegacyIterations,
            HashAlgorithmName.SHA512,
            PinnedHashSize);

        _sut.VerifyPassword(password, expected, salt).Should().BeTrue(
            "every hash written before the versioned format is a bare 64-byte digest at 600,000 iterations, "
            + "and those accounts must keep signing in until they are rehashed");
        _sut.NeedsRehash(expected).Should().BeTrue(
            "a legacy digest is exactly what the sign-in rehash exists to upgrade");
    }

    [Fact]
    public void VerifyPassword_AcceptsAVersionedDigestComputedWithThePinnedPbkdf2Parameters()
    {
        const string password = "KnownAnswerPassword123";
        var salt = DeterministicSalt();
        var hash = Versioned(PinnedFormatVersion, PinnedIterations, Pbkdf2(password, salt, PinnedIterations));

        _sut.VerifyPassword(password, hash, salt).Should().BeTrue(
            "a swapped hash algorithm or a changed output length would stop matching this independently "
            + "computed digest, so weakening the KDF fails the build instead of quietly making every stored "
            + "credential cheaper to crack offline");
        _sut.NeedsRehash(hash).Should().BeFalse("a hash at the current version and work factor is current");
    }

    [Fact]
    public void HashPassword_ProducesTheVersionedDigestThePinnedPbkdf2ParametersProduce()
    {
        const string password = "KnownAnswerPassword123";

        var (hash, salt) = _sut.HashPassword(password);

        hash.Should().HaveCount(PinnedVersionedHashSize,
            "1 version byte, a 4-byte big-endian iteration count, then the full 64-byte digest");
        hash[0].Should().Be(PinnedFormatVersion);
        BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(1, 4)).Should().Be(PinnedIterations,
            "new hashes carry the OWASP PBKDF2-HMAC-SHA512 work factor");
        hash.AsSpan(5).ToArray().Should().Equal(Pbkdf2(password, salt, PinnedIterations),
            "the hash path must derive its digest with PBKDF2-HMAC-SHA512 at 210,000 iterations: any other "
            + "algorithm or work factor produces different bytes here");
        salt.Should().HaveCount(PinnedSaltSize,
            "a shortened salt makes precomputed rainbow tables viable again");
    }

    [Fact]
    public void VerifyPassword_HonorsTheIterationCountStoredInTheHash()
    {
        const string password = "KnownAnswerPassword123";
        var salt = DeterministicSalt();
        var hash = Versioned(PinnedFormatVersion, 300_000, Pbkdf2(password, salt, 300_000));

        _sut.VerifyPassword(password, hash, salt).Should().BeTrue(
            "each record carries its own work factor, so raising the current one later does not lock out "
            + "accounts stored at the old one");
        _sut.NeedsRehash(hash).Should().BeTrue("a stored count other than the current one is upgraded on sign-in");
    }

    [Theory]
    [InlineData(PinnedFormatVersion, 99_999)]
    [InlineData(PinnedFormatVersion, 2_000_001)]
    [InlineData(PinnedFormatVersion, 0)]
    [InlineData(PinnedFormatVersion, -1)]
    [InlineData(0x00, PinnedIterations)]
    [InlineData(0x02, PinnedIterations)]
    public void VerifyPassword_WithAnOutOfRangeCountOrAnUnknownVersion_IsRejected(byte version, int iterations)
    {
        const string password = "KnownAnswerPassword123";
        var salt = DeterministicSalt();

        // The digest is derived at the count the header claims (falling back to the current count when
        // the claim is not a positive number), so only the header can be what rejects it.
        var deriveAt = iterations > 0 ? iterations : PinnedIterations;
        var hash = Versioned(version, iterations, Pbkdf2(password, salt, deriveAt));

        _sut.VerifyPassword(password, hash, salt).Should().BeFalse(
            "a header this hasher never writes must verify nothing: a tiny count would make a tampered "
            + "row cheap to guess and a huge one would turn a sign-in into a CPU denial of service");
        _sut.NeedsRehash(hash).Should().BeFalse("material that cannot verify is never rehashed");
    }

    [Fact]
    public void VerifyPassword_RejectsADigestComputedWithAWeakerIterationCount()
    {
        const string password = "KnownAnswerPassword123";
        var salt = DeterministicSalt();
        var weakDigest = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            100_000,
            HashAlgorithmName.SHA512,
            PinnedHashSize);

        _sut.VerifyPassword(password, weakDigest, salt).Should().BeFalse(
            "the iteration count must actually participate in verification: if a digest derived at a "
            + "fraction of the work factor verified, an attacker who downgraded the stored work factor "
            + "would still authenticate");
    }

    // ── Constant pins (the invariants, read straight off the type) ──
    [Fact]
    public void Iterations_IsPinnedToTheOwaspRecommendedWorkFactor() =>
        ReadPrivateConstant("Iterations").Should().Be(PinnedIterations,
            "the work factor is the entire defense against offline brute force; lowering it is a security "
            + "change that must be argued for, not an edit that slips through");

    [Fact]
    public void LegacyIterations_IsPinnedTo600000() =>
        ReadPrivateConstant("LegacyIterations").Should().Be(PinnedLegacyIterations,
            "every header-less hash in storage was derived at 600,000 iterations; any other value here locks "
            + "every legacy account out");

    [Fact]
    public void SaltSize_IsPinnedTo32Bytes() =>
        ReadPrivateConstant("SaltSize").Should().Be(PinnedSaltSize,
            "256 bits of per-credential salt is what keeps precomputation and cross-account correlation "
            + "off the table");

    [Fact]
    public void HashSize_IsPinnedTo64Bytes() =>
        ReadPrivateConstant("HashSize").Should().Be(PinnedHashSize,
            "the stored digest must keep the full 512-bit output of the KDF, not a truncation of it");

    [Fact]
    public void VerifyPassword_RejectsALegacyHmacDigest()
    {
        const string password = "LegacyPassword";
        using var hmac = new HMACSHA512();
        var legacySalt = hmac.Key; // 128 bytes, the length the removed legacy branch keyed on
        var legacyDigest = hmac.ComputeHash(Encoding.UTF8.GetBytes(password));

        _sut.VerifyPassword(password, legacyDigest, legacySalt).Should().BeFalse(
            "the HMAC-SHA512 verification branch is gone: a credential still stored in the legacy shape "
            + "must fail verification rather than authenticate through an unsalted, single-round digest");
    }

    [Fact]
    // The empty pair is the shape an external-OAuth account carries (ADR-036). Deriving an output
    // the length of the stored hash produced an empty span, and a fixed-time comparison of two empty
    // spans is true, so this row used to verify against ANY password.
    public void VerifyPassword_AgainstAnEmptyStoredHashAndSalt_IsRejected() =>
        _sut.VerifyPassword("literally anything", [], []).Should().BeFalse(
            "an account with no stored credential material must not authenticate by password");

    [Theory]
    [InlineData(0, PinnedSaltSize)]
    [InlineData(PinnedHashSize, 0)]
    [InlineData(PinnedHashSize - 1, PinnedSaltSize)]
    [InlineData(PinnedHashSize + 1, PinnedSaltSize)]
    [InlineData(PinnedHashSize, PinnedSaltSize - 1)]
    [InlineData(PinnedHashSize, PinnedSaltSize + 1)]
    [InlineData(PinnedVersionedHashSize - 1, PinnedSaltSize)]
    [InlineData(PinnedVersionedHashSize + 1, PinnedSaltSize)]
    [InlineData(PinnedVersionedHashSize, PinnedSaltSize - 1)]
    [InlineData(PinnedVersionedHashSize, 0)]
    public void VerifyPassword_WithNonCanonicalMaterial_IsRejected(int hashLength, int saltLength)
    {
        var hash = new byte[hashLength];
        var salt = new byte[saltLength];

        _sut.VerifyPassword("any password", hash, salt).Should().BeFalse(
            "material this hasher never produced verifies nothing: only {0}-byte legacy or {1}-byte versioned "
            + "hashes over exactly {2}-byte salts are candidates",
            PinnedHashSize,
            PinnedVersionedHashSize,
            PinnedSaltSize);
    }

    [Fact]
    public void VerifyPassword_WithAValidHeaderOverAnOddSalt_IsRejected()
    {
        const string password = "KnownAnswerPassword123";
        var salt = DeterministicSalt();
        var hash = Versioned(PinnedFormatVersion, PinnedIterations, Pbkdf2(password, salt, PinnedIterations));

        _sut.VerifyPassword(password, hash, salt[..^1]).Should().BeFalse(
            "the salt length guard applies to the versioned shape exactly as to the legacy one");
    }

    [Fact]
    public void VerifyPassword_StillAcceptsTheMaterialItProduces()
    {
        var (hash, salt) = _sut.HashPassword("correct horse battery staple");

        _sut.VerifyPassword("correct horse battery staple", hash, salt).Should().BeTrue(
            "the length guard must reject non-canonical material without breaking the real path");
    }

    private static byte[] Pbkdf2(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA512,
            PinnedHashSize);

    private static byte[] Versioned(byte version, int iterations, byte[] digest)
    {
        var hash = new byte[PinnedVersionedHashSize];
        hash[0] = version;
        BinaryPrimitives.WriteInt32BigEndian(hash.AsSpan(1, 4), iterations);
        digest.CopyTo(hash.AsSpan(5));
        return hash;
    }

    private static byte[] DeterministicSalt()
    {
        var salt = new byte[PinnedSaltSize];
        for (var i = 0; i < salt.Length; i++)
        {
            salt[i] = (byte)(i * 7 + 3);
        }

        return salt;
    }

    private static int ReadPrivateConstant(string name)
    {
        FieldInfo? field = typeof(PasswordHasher).GetField(name, BindingFlags.NonPublic | BindingFlags.Static);

        field.Should().NotBeNull($"'{name}' is the constant this invariant is written against; renaming or "
            + "removing it must fail here rather than silently drop the pin");

        return (int)field!.GetValue(null)!;
    }
}

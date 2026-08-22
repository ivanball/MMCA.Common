using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using MMCA.Common.Infrastructure.Services;

namespace MMCA.Common.Infrastructure.Tests.Services;

/// <summary>
/// Executable security invariants for <see cref="PasswordHasher"/>. The behavioural round-trips live in
/// <see cref="PasswordHasherTests"/> and stay green even if every cryptographic parameter is weakened,
/// because a hasher that hashes and verifies with the same weak parameters still round-trips. These tests
/// pin the parameters themselves: two known-answer tests recompute the digest independently with the
/// OWASP-recommended settings (PBKDF2-HMAC-SHA512, 600,000 iterations, 32-byte salt, 64-byte output), a
/// negative known-answer test proves the iteration count actually participates in verification, and
/// reflection pins the private constants so lowering one fails the build instead of silently shipping.
/// </summary>
public sealed class PasswordHasherSecurityTests
{
    private const int PinnedIterations = 600_000;
    private const int PinnedSaltSize = 32;
    private const int PinnedHashSize = 64;
    private const int PinnedLegacyHmacSaltSize = 128;

    private readonly PasswordHasher _sut = new();

    // ── Known-answer tests (the parameters, not the round-trip) ──
    [Fact]
    public void VerifyPassword_AcceptsADigestComputedWithThePinnedPbkdf2Parameters()
    {
        const string password = "KnownAnswerPassword123";
        var salt = DeterministicSalt();
        var expected = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            PinnedIterations,
            HashAlgorithmName.SHA512,
            PinnedHashSize);

        _sut.VerifyPassword(password, expected, salt).Should().BeTrue(
            "a lowered iteration count, a swapped hash algorithm or a changed output length would stop "
            + "matching this independently computed digest, so weakening the KDF fails the build instead "
            + "of quietly making every stored credential cheaper to crack offline");
    }

    [Fact]
    public void HashPassword_ProducesTheDigestThePinnedPbkdf2ParametersProduce()
    {
        const string password = "KnownAnswerPassword123";

        var (hash, salt) = _sut.HashPassword(password);

        var expected = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            PinnedIterations,
            HashAlgorithmName.SHA512,
            PinnedHashSize);

        hash.Should().Equal(expected,
            "the hash path must derive its digest with PBKDF2-HMAC-SHA512 at 600,000 iterations: any other "
            + "algorithm or work factor produces different bytes here");
        salt.Should().HaveCount(PinnedSaltSize,
            "a shortened salt makes precomputed rainbow tables viable again");
        hash.Should().HaveCount(PinnedHashSize,
            "a truncated digest throws away entropy an attacker would otherwise have to search");
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
    public void SaltSize_IsPinnedTo32Bytes() =>
        ReadPrivateConstant("SaltSize").Should().Be(PinnedSaltSize,
            "256 bits of per-credential salt is what keeps precomputation and cross-account correlation "
            + "off the table");

    [Fact]
    public void HashSize_IsPinnedTo64Bytes() =>
        ReadPrivateConstant("HashSize").Should().Be(PinnedHashSize,
            "the stored digest must keep the full 512-bit output of the KDF, not a truncation of it");

    [Fact]
    public void LegacyHmacSaltSize_IsPinnedTo128Bytes() =>
        ReadPrivateConstant("LegacyHmacSaltSize").Should().Be(PinnedLegacyHmacSaltSize,
            "this length is the discriminator that routes a credential to the weak legacy HMAC path: "
            + "moving it to 32 would send every current PBKDF2 credential down the unsalted-HMAC branch");

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

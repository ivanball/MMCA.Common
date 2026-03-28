using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using MMCA.Common.Infrastructure.Services;

namespace MMCA.Common.Infrastructure.Tests.Services;

public sealed class PasswordHasherTests
{
    private readonly PasswordHasher _sut = new();

    // ── HashPassword (PBKDF2) ──
    [Fact]
    public void HashPassword_ReturnsNonNullHashAndSalt()
    {
        var (hash, salt) = _sut.HashPassword("SecurePassword123");

        hash.Should().NotBeNullOrEmpty();
        salt.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void HashPassword_Produces64ByteHashAnd32ByteSalt()
    {
        var (hash, salt) = _sut.HashPassword("SecurePassword123");

        hash.Should().HaveCount(64);
        salt.Should().HaveCount(32);
    }

    [Fact]
    public void HashPassword_ProducesDifferentSaltsForSamePassword()
    {
        var (_, salt1) = _sut.HashPassword("SamePassword");
        var (_, salt2) = _sut.HashPassword("SamePassword");

        salt1.Should().NotEqual(salt2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void HashPassword_WithNullOrEmptyPassword_Throws(string? password)
    {
        var act = () => _sut.HashPassword(password!);
        act.Should().Throw<ArgumentException>();
    }

    // ── VerifyPassword (PBKDF2) ──
    [Fact]
    public void VerifyPassword_CorrectPassword_ReturnsTrue()
    {
        var (hash, salt) = _sut.HashPassword("MyPassword");
        _sut.VerifyPassword("MyPassword", hash, salt).Should().BeTrue();
    }

    [Fact]
    public void VerifyPassword_WrongPassword_ReturnsFalse()
    {
        var (hash, salt) = _sut.HashPassword("MyPassword");
        _sut.VerifyPassword("WrongPassword", hash, salt).Should().BeFalse();
    }

    // ── Legacy HMAC-SHA512 backward compatibility ──
    [Fact]
    public void VerifyPassword_LegacyHmacSha512Hash_ReturnsTrue()
    {
        // Simulate a password hashed with the old HMAC-SHA512 algorithm (128-byte salt).
        const string password = "LegacyPassword";
        using var hmac = new HMACSHA512();
        var legacySalt = hmac.Key; // 128 bytes
        var legacyHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(password));

        _sut.VerifyPassword(password, legacyHash, legacySalt).Should().BeTrue();
    }

    [Fact]
    public void VerifyPassword_LegacyHmacSha512Hash_WrongPassword_ReturnsFalse()
    {
        const string password = "LegacyPassword";
        using var hmac = new HMACSHA512();
        var legacySalt = hmac.Key;
        var legacyHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(password));

        _sut.VerifyPassword("WrongPassword", legacyHash, legacySalt).Should().BeFalse();
    }

    // ── Argument validation ──
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void VerifyPassword_WithNullOrEmptyPassword_Throws(string? password)
    {
        var (hash, salt) = _sut.HashPassword("original");
        var act = () => _sut.VerifyPassword(password!, hash, salt);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void VerifyPassword_WithNullHash_Throws()
    {
        var act = () => _sut.VerifyPassword("password", null!, [1, 2, 3]);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void VerifyPassword_WithNullSalt_Throws()
    {
        var act = () => _sut.VerifyPassword("password", [1, 2, 3], null!);
        act.Should().Throw<ArgumentNullException>();
    }
}

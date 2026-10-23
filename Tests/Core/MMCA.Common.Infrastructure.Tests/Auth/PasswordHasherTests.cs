using AwesomeAssertions;
using MMCA.Common.Infrastructure.Auth;

namespace MMCA.Common.Infrastructure.Tests.Auth;

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
    public void HashPassword_Produces69ByteVersionedHashAnd32ByteSalt()
    {
        var (hash, salt) = _sut.HashPassword("SecurePassword123");

        hash.Should().HaveCount(69, "a 1-byte version, a 4-byte iteration count and the 64-byte PBKDF2 output");
        salt.Should().HaveCount(32);
    }

    // ── NeedsRehash ──
    [Fact]
    public void NeedsRehash_ForAHashItJustProduced_IsFalse()
    {
        var (hash, _) = _sut.HashPassword("SecurePassword123");

        _sut.NeedsRehash(hash).Should().BeFalse();
    }

    [Fact]
    public void NeedsRehash_ForALegacy64ByteHash_IsTrue() =>
        _sut.NeedsRehash(new byte[64]).Should().BeTrue();

    [Fact]
    public void NeedsRehash_ForUnrecognizedMaterial_IsFalse()
    {
        _sut.NeedsRehash([]).Should().BeFalse();
        _sut.NeedsRehash(new byte[65]).Should().BeFalse();
    }

    [Fact]
    public void NeedsRehash_WithNullHash_Throws()
    {
        var act = () => _sut.NeedsRehash(null!);
        act.Should().Throw<ArgumentNullException>();
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

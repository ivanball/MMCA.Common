using AwesomeAssertions;
using MMCA.Common.Infrastructure.Services;

namespace MMCA.Common.Infrastructure.Tests.Services;

public sealed class PasswordHasherTests
{
    private readonly PasswordHasher _sut = new();

    // ── HashPassword ──
    [Fact]
    public void HashPassword_ReturnsNonNullHashAndSalt()
    {
        var (hash, salt) = _sut.HashPassword("SecurePassword123");

        hash.Should().NotBeNullOrEmpty();
        salt.Should().NotBeNullOrEmpty();
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

    // ── VerifyPassword ──
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

using AwesomeAssertions;
using MMCA.Common.Shared.ValueObjects;

namespace MMCA.Common.Shared.Tests.ValueObjects;

public class EmailTests
{
    [Fact]
    public void Create_WithValidEmail_ReturnsSuccess()
    {
        var result = Email.Create("user@example.com");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Value.Should().Be("user@example.com");
    }

    [Fact]
    public void Create_NormalizesToLowercase()
    {
        var result = Email.Create("User@Example.COM");

        result.Value!.Value.Should().Be("user@example.com");
    }

    [Fact]
    public void Create_TrimsWhitespace()
    {
        var result = Email.Create("  user@example.com  ");

        result.Value!.Value.Should().Be("user@example.com");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void Create_WithEmptyOrNull_ReturnsFailure(string? email)
    {
        var result = Email.Create(email!);

        result.IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData("notanemail")]
    [InlineData("@example.com")]
    [InlineData("user@")]
    [InlineData("user@example")]
    public void Create_WithInvalidFormat_ReturnsFailure(string email)
    {
        var result = Email.Create(email);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void ImplicitConversion_ReturnsValue()
    {
        Email email = Email.Create("test@example.com").Value!;
        string value = email;

        value.Should().Be("test@example.com");
    }

    [Fact]
    public void EqualEmails_AreEqual()
    {
        var a = Email.Create("user@example.com").Value!;
        var b = Email.Create("USER@example.com").Value!;

        a.Should().Be(b);
    }

    [Fact]
    public void DifferentEmails_AreNotEqual()
    {
        var a = Email.Create("alice@example.com").Value!;
        var b = Email.Create("bob@example.com").Value!;

        a.Should().NotBe(b);
    }
}

using AwesomeAssertions;
using MMCA.Common.Shared.ValueObjects;

namespace MMCA.Common.Shared.Tests.ValueObjects;

public class PhoneNumberTests
{
    [Theory]
    [InlineData("1234567")]
    [InlineData("+1 (555) 123-4567")]
    [InlineData("555-123-4567")]
    public void Create_WithValidPhoneNumber_ReturnsSuccess(string number)
    {
        var result = PhoneNumber.Create(number);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Create_TrimsWhitespace()
    {
        var result = PhoneNumber.Create("  555-1234567  ");

        result.Value!.Value.Should().Be("555-1234567");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void Create_WithEmptyOrNull_ReturnsFailure(string? number)
    {
        var result = PhoneNumber.Create(number!);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_WithTooShort_ReturnsFailure()
    {
        var result = PhoneNumber.Create("123456");

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_WithTooLong_ReturnsFailure()
    {
        var result = PhoneNumber.Create("123456789012345678901");

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_WithInvalidCharacters_ReturnsFailure()
    {
        var result = PhoneNumber.Create("555-ABC-1234");

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void ImplicitConversion_ReturnsValue()
    {
        PhoneNumber phone = PhoneNumber.Create("+1 555-1234567").Value!;
        string value = phone;

        value.Should().Be("+1 555-1234567");
    }

    [Fact]
    public void EqualPhoneNumbers_AreEqual()
    {
        var a = PhoneNumber.Create("555-1234567").Value!;
        var b = PhoneNumber.Create("555-1234567").Value!;

        a.Should().Be(b);
    }
}

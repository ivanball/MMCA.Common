using AwesomeAssertions;
using MMCA.Common.Shared.Extensions;

namespace MMCA.Common.Shared.Tests.Extensions;

public class DomainHelperTests
{
    // ── String ──
    [Fact]
    public void Parse_String_ReturnsValue() =>
        "hello".Parse<string>().Should().Be("hello");

    [Fact]
    public void Parse_NullString_ReturnsEmpty() =>
        ((string?)null).Parse<string>().Should().Be(string.Empty);

    // ── Int ──
    [Fact]
    public void Parse_ValidInt_ReturnsParsedValue() =>
        "42".Parse<int>().Should().Be(42);

    [Fact]
    public void Parse_InvalidInt_ReturnsZero() =>
        "abc".Parse<int>().Should().Be(0);

    [Fact]
    public void Parse_NullInt_ReturnsDefault() =>
        ((string?)null).Parse<int>().Should().Be(0);

    // ── Long ──
    [Fact]
    public void Parse_ValidLong_ReturnsParsedValue() =>
        "9999999999".Parse<long>().Should().Be(9_999_999_999L);

    [Fact]
    public void Parse_InvalidLong_ReturnsZero() =>
        "xyz".Parse<long>().Should().Be(0L);

    // ── Ulong ──
    [Fact]
    public void Parse_ValidUlong_ReturnsParsedValue() =>
        "18446744073709551615".Parse<ulong>().Should().Be(ulong.MaxValue);

    // ── Guid ──
    [Fact]
    public void Parse_ValidGuid_ReturnsParsedGuid()
    {
        var guid = Guid.NewGuid();
        guid.ToString().Parse<Guid>().Should().Be(guid);
    }

    [Fact]
    public void Parse_InvalidGuid_ReturnsGuidEmpty() =>
        "not-a-guid".Parse<Guid>().Should().Be(Guid.Empty);

    // ── Bool ──
    [Fact]
    public void Parse_TrueString_ReturnsTrue() =>
        "true".Parse<bool>().Should().BeTrue();

    [Fact]
    public void Parse_FalseString_ReturnsFalse() =>
        "false".Parse<bool>().Should().BeFalse();

    [Fact]
    public void Parse_InvalidBool_ReturnsFalse() =>
        "maybe".Parse<bool>().Should().BeFalse();

    // ── Enum ──
    [Fact]
    public void Parse_ValidEnum_ReturnsParsedValue() =>
        "Monday".Parse<DayOfWeek>().Should().Be(DayOfWeek.Monday);

    [Fact]
    public void Parse_CaseInsensitiveEnum_ReturnsParsedValue() =>
        "friday".Parse<DayOfWeek>().Should().Be(DayOfWeek.Friday);

    // ── Whitespace / null ──
    [Fact]
    public void Parse_WhitespaceForNonString_ReturnsDefault() =>
        "  ".Parse<int>().Should().Be(0);

    // ── Unsupported type ──
    [Fact]
    public void Parse_UnsupportedType_ThrowsFormatException() =>
        FluentActions.Invoking(() => "1.5".Parse<decimal>())
            .Should().Throw<FormatException>();

    // ── TryParse: String ──
    [Fact]
    public void TryParse_String_ReturnsTrueAndValue()
    {
        "hello".TryParse<string>(out var value).Should().BeTrue();
        value.Should().Be("hello");
    }

    [Fact]
    public void TryParse_NullString_ReturnsFalseAndEmpty()
    {
        ((string?)null).TryParse<string>(out var value).Should().BeFalse();
        value.Should().Be(string.Empty);
    }

    // ── TryParse: Int ──
    [Fact]
    public void TryParse_ValidInt_ReturnsTrueAndValue()
    {
        "42".TryParse<int>(out var value).Should().BeTrue();
        value.Should().Be(42);
    }

    [Fact]
    public void TryParse_InvalidInt_ReturnsFalseAndZero()
    {
        "abc".TryParse<int>(out var value).Should().BeFalse();
        value.Should().Be(0);
    }

    [Fact]
    public void TryParse_NullInt_ReturnsFalseAndZero()
    {
        ((string?)null).TryParse<int>(out var value).Should().BeFalse();
        value.Should().Be(0);
    }

    // ── TryParse: Long ──
    [Fact]
    public void TryParse_ValidLong_ReturnsTrueAndValue()
    {
        "9999999999".TryParse<long>(out var value).Should().BeTrue();
        value.Should().Be(9_999_999_999L);
    }

    [Fact]
    public void TryParse_InvalidLong_ReturnsFalseAndZero()
    {
        "xyz".TryParse<long>(out var value).Should().BeFalse();
        value.Should().Be(0L);
    }

    // ── TryParse: Ulong ──
    [Fact]
    public void TryParse_ValidUlong_ReturnsTrueAndValue()
    {
        "18446744073709551615".TryParse<ulong>(out var value).Should().BeTrue();
        value.Should().Be(ulong.MaxValue);
    }

    [Fact]
    public void TryParse_InvalidUlong_ReturnsFalseAndZero()
    {
        "-1".TryParse<ulong>(out var value).Should().BeFalse();
        value.Should().Be(0UL);
    }

    // ── TryParse: Guid ──
    [Fact]
    public void TryParse_ValidGuid_ReturnsTrueAndValue()
    {
        var guid = Guid.NewGuid();

        guid.ToString().TryParse<Guid>(out var value).Should().BeTrue();
        value.Should().Be(guid);
    }

    [Fact]
    public void TryParse_InvalidGuid_ReturnsFalseAndEmpty()
    {
        "not-a-guid".TryParse<Guid>(out var value).Should().BeFalse();
        value.Should().Be(Guid.Empty);
    }

    // ── TryParse: Bool (the case Parse cannot distinguish) ──
    [Fact]
    public void TryParse_TrueString_ReturnsTrueAndTrue()
    {
        "true".TryParse<bool>(out var value).Should().BeTrue();
        value.Should().BeTrue();
    }

    [Fact]
    public void TryParse_FalseString_ReturnsTrueAndFalse()
    {
        "false".TryParse<bool>(out var value).Should().BeTrue();
        value.Should().BeFalse();
    }

    [Fact]
    public void TryParse_InvalidBool_ReturnsFalse()
    {
        "maybe".TryParse<bool>(out var value).Should().BeFalse();
        value.Should().BeFalse();
    }

    // ── TryParse: Enum ──
    [Fact]
    public void TryParse_ValidEnum_ReturnsTrueAndValue()
    {
        "Monday".TryParse<DayOfWeek>(out var value).Should().BeTrue();
        value.Should().Be(DayOfWeek.Monday);
    }

    [Fact]
    public void TryParse_CaseInsensitiveEnum_ReturnsTrueAndValue()
    {
        "friday".TryParse<DayOfWeek>(out var value).Should().BeTrue();
        value.Should().Be(DayOfWeek.Friday);
    }

    [Fact]
    public void TryParse_InvalidEnum_ReturnsFalseAndDefault()
    {
        "notaday".TryParse<DayOfWeek>(out var value).Should().BeFalse();
        value.Should().Be(DayOfWeek.Sunday); // the enum default, which Parse cannot distinguish from a real "Sunday"
    }

    // ── TryParse: Whitespace / empty ──
    [Fact]
    public void TryParse_WhitespaceForNonString_ReturnsFalseAndDefault()
    {
        "  ".TryParse<int>(out var value).Should().BeFalse();
        value.Should().Be(0);
    }

    [Fact]
    public void TryParse_EmptyForNonString_ReturnsFalseAndDefault()
    {
        string.Empty.TryParse<Guid>(out var value).Should().BeFalse();
        value.Should().Be(Guid.Empty);
    }

    // ── TryParse: Unsupported type ──
    [Fact]
    public void TryParse_UnsupportedType_ThrowsFormatException() =>
        FluentActions.Invoking(() => "1.5".TryParse<decimal>(out _))
            .Should().Throw<FormatException>();
}

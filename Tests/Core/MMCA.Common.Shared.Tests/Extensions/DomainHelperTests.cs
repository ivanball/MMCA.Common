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
}

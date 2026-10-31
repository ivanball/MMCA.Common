using AwesomeAssertions;
using MMCA.Common.Shared.ValueObjects.Contact;

namespace MMCA.Common.Shared.Tests.ValueObjects.Contact;

/// <summary>
/// <see cref="UsPostalRegions"/>: the 50 states, DC, the five territories and the three military
/// regions by USPS code, code/name lookup, and the ZIP / ZIP+4 format.
/// </summary>
public sealed class UsPostalRegionsTests
{
    [Fact]
    public void All_HoldsTheStatesDcTerritoriesAndMilitaryRegions_Once()
    {
        UsPostalRegions.All.Should().HaveCount(59, "50 states + DC + 5 territories + 3 military regions");
        UsPostalRegions.All.Select(region => region.Code).Should().OnlyHaveUniqueItems();
        UsPostalRegions.All.Select(region => region.Code).Should().Contain(["DC", "PR", "VI", "GU", "AS", "MP", "AA", "AE", "AP"]);
        UsPostalRegions.All.Should().OnlyContain(region => region.Code.Length == 2 && region.Code.All(char.IsAsciiLetterUpper));
    }

    [Theory]
    [InlineData("GA", true)]
    [InlineData("ga", true)]
    [InlineData(" AE ", true)]
    [InlineData("XX", false)]
    [InlineData("Georgia", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsKnownCode_AcceptsOnlyRegionCodes(string? code, bool expected) =>
        UsPostalRegions.IsKnownCode(code).Should().Be(expected);

    [Theory]
    [InlineData("GA", "GA")]
    [InlineData("pr", "PR")]
    [InlineData("Georgia", "GA")]
    [InlineData("district of columbia", "DC")]
    [InlineData("Narnia", null)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void ToCode_ReadsACodeOrAName(string? value, string? expected) =>
        UsPostalRegions.ToCode(value).Should().Be(expected);

    [Theory]
    [InlineData("30135", true)]
    [InlineData("30135-1234", true)]
    [InlineData(" 30135 ", true)]
    [InlineData("3013", false)]
    [InlineData("301351", false)]
    [InlineData("30135-12", false)]
    [InlineData("ABCDE", false)]
    [InlineData("30135 1234", false)]
    [InlineData(null, false)]
    public void IsValidZipCode_AcceptsFiveDigitsOrZipPlusFour(string? zip, bool expected) =>
        UsPostalRegions.IsValidZipCode(zip).Should().Be(expected);

    // L251: .NET's \d matches any Unicode decimal digit, so Arabic-Indic (U+0660..U+0669) or
    // full-width (U+FF10..U+FF19) digits passed as a ZIP. A ZIP code is ASCII digits only.
    [Theory]
    [InlineData("\u0663\u0660\u0661\u0663\u0665")]
    [InlineData("30135-\u0661\u0662\u0663\u0664")]
    [InlineData("\uFF13\uFF10\uFF11\uFF13\uFF15")]
    public void IsValidZipCode_RejectsNonAsciiDigits(string zip) =>
        UsPostalRegions.IsValidZipCode(zip).Should().BeFalse();
}

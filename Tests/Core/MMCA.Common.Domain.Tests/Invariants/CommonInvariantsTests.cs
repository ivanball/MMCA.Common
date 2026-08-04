using AwesomeAssertions;
using MMCA.Common.Domain.Invariants;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Globalization;
using MMCA.Common.Shared.ValueObjects;

namespace MMCA.Common.Domain.Tests.Invariants;

public sealed class CommonInvariantsTests
{
    // ── EnsureStringIsNotEmpty ──
    [Fact]
    public void EnsureStringIsNotEmpty_WithValidString_ReturnsSuccess()
    {
        Result result = CommonInvariants.EnsureStringIsNotEmpty(
            "valid", "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EnsureStringIsNotEmpty_WithEmptyOrWhitespace_ReturnsFailure(string? value)
    {
        Result result = CommonInvariants.EnsureStringIsNotEmpty(
            value!, "Test.Code", "Name cannot be empty.", "Create", "Name");

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("Test.Code");
        result.Errors[0].Message.Should().Be("Name cannot be empty.");
        result.Errors[0].Type.Should().Be(ErrorType.Invariant);
    }

    // ── EnsureStringMaxLength ──
    [Fact]
    public void EnsureStringMaxLength_WhenWithinLimit_ReturnsSuccess()
    {
        Result result = CommonInvariants.EnsureStringMaxLength(
            "short", 10, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void EnsureStringMaxLength_WhenExceedsLimit_ReturnsFailure()
    {
        Result result = CommonInvariants.EnsureStringMaxLength(
            "this is too long", 5, "Test.TooLong", "Too long.", "Create", "Name");

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("Test.TooLong");
    }

    [Fact]
    public void EnsureStringMaxLength_WhenNull_ReturnsSuccess()
    {
        Result result = CommonInvariants.EnsureStringMaxLength(
            null, 10, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void EnsureStringMaxLength_WhenEmpty_ReturnsSuccess()
    {
        Result result = CommonInvariants.EnsureStringMaxLength(
            string.Empty, 10, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    // ── EnsureIdIsNotDefault ──
    [Fact]
    public void EnsureIdIsNotDefault_WithValidInt_ReturnsSuccess()
    {
        Result result = CommonInvariants.EnsureIdIsNotDefault(
            42, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void EnsureIdIsNotDefault_WithDefaultInt_ReturnsFailure()
    {
        Result result = CommonInvariants.EnsureIdIsNotDefault(
            0, "Test.InvalidId", "ID must be provided.", "Create", "Id");

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("Test.InvalidId");
        result.Errors[0].Type.Should().Be(ErrorType.Invariant);
    }

    [Fact]
    public void EnsureIdIsNotDefault_WithValidGuid_ReturnsSuccess()
    {
        Result result = CommonInvariants.EnsureIdIsNotDefault(
            Guid.NewGuid(), "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void EnsureIdIsNotDefault_WithDefaultGuid_ReturnsFailure()
    {
        Result result = CommonInvariants.EnsureIdIsNotDefault(
            Guid.Empty, "Test.InvalidGuid", "GUID required.", "Create", "Id");

        result.IsFailure.Should().BeTrue();
    }

    // ── EnsureBytesAreNotEmpty ──
    [Fact]
    public void EnsureBytesAreNotEmpty_WithData_ReturnsSuccess()
    {
        Result result = CommonInvariants.EnsureBytesAreNotEmpty(
            [1, 2, 3], "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void EnsureBytesAreNotEmpty_WithEmptyArray_ReturnsFailure()
    {
        Result result = CommonInvariants.EnsureBytesAreNotEmpty(
            [], "Test.Empty", "Data is required.", "Upload", "File");

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("Test.Empty");
    }

    [Fact]
    public void EnsureBytesAreNotEmpty_WithNull_ReturnsFailure()
    {
        Result result = CommonInvariants.EnsureBytesAreNotEmpty(
            null!, "Test.Null", "Data is required.", "Upload", "File");

        result.IsFailure.Should().BeTrue();
    }

    // ── EnsureIntIsPositive ──
    [Fact]
    public void EnsureIntIsPositive_WithPositiveValue_ReturnsSuccess()
    {
        Result result = CommonInvariants.EnsureIntIsPositive(
            1, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void EnsureIntIsPositive_WithZeroOrNegative_ReturnsFailure(int value)
    {
        Result result = CommonInvariants.EnsureIntIsPositive(
            value, "Test.Quantity.NotPositive", "Quantity must be positive.", "Create", "Quantity");

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("Test.Quantity.NotPositive");
        result.Errors[0].Message.Should().Be("Quantity must be positive.");
        result.Errors[0].Type.Should().Be(ErrorType.Invariant);
    }

    // ── EnsureMoneyIsNotNegative ──
    [Fact]
    public void EnsureMoneyIsNotNegative_WithPositiveAmount_ReturnsSuccess()
    {
        Money money = Money.Create(10.50m, Currency.Usd).Value!;

        Result result = CommonInvariants.EnsureMoneyIsNotNegative(
            money, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void EnsureMoneyIsNotNegative_WithZeroAmount_ReturnsSuccess()
    {
        var money = Money.Zero(Currency.Usd);

        Result result = CommonInvariants.EnsureMoneyIsNotNegative(
            money, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void EnsureMoneyIsNotNegative_WithNegativeAmount_ReturnsFailure()
    {
        Money money = Money.Create(-0.01m, Currency.Eur).Value!;

        Result result = CommonInvariants.EnsureMoneyIsNotNegative(
            money, "Test.Price.Negative", "Price cannot be negative.", "Create", "Price");

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("Test.Price.Negative");
        result.Errors[0].Type.Should().Be(ErrorType.Invariant);
    }

    [Fact]
    public void EnsureMoneyIsNotNegative_WithNull_ReturnsFailure()
    {
        Result result = CommonInvariants.EnsureMoneyIsNotNegative(
            null!, "Test.Price.Missing", "Price is required.", "Create", "Price");

        result.IsFailure.Should().BeTrue();
    }

    // ── EnsureCollectionIsNotEmpty ──
    [Fact]
    public void EnsureCollectionIsNotEmpty_WithItems_ReturnsSuccess()
    {
        Result result = CommonInvariants.EnsureCollectionIsNotEmpty<string>(
            ["line"], "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void EnsureCollectionIsNotEmpty_WithEmptyCollection_ReturnsFailure()
    {
        Result result = CommonInvariants.EnsureCollectionIsNotEmpty<string>(
            [], "Test.Lines.Empty", "Order must not be empty.", "Create", "OrderLines");

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("Test.Lines.Empty");
        result.Errors[0].Type.Should().Be(ErrorType.Invariant);
    }

    [Fact]
    public void EnsureCollectionIsNotEmpty_WithNull_ReturnsFailure()
    {
        Result result = CommonInvariants.EnsureCollectionIsNotEmpty<string>(
            null!, "Test.Lines.Null", "Order must not be empty.", "Create", "OrderLines");

        result.IsFailure.Should().BeTrue();
    }

    // ── EnsurePreferredCultureIsValid ──
    [Fact]
    public void EnsurePreferredCultureIsValid_WithNull_ReturnsSuccess()
    {
        Result result = CommonInvariants.EnsurePreferredCultureIsValid(
            null, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void EnsurePreferredCultureIsValid_WithSupportedCulture_ReturnsSuccess()
    {
        Result result = CommonInvariants.EnsurePreferredCultureIsValid(
            SupportedCultures.Default, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("xx-YY")]
    public void EnsurePreferredCultureIsValid_WithUnsupportedCulture_ReturnsFailure(string culture)
    {
        Result result = CommonInvariants.EnsurePreferredCultureIsValid(
            culture, "Test.Culture.Invalid", "Culture is not supported.", "SetPreferences", "Culture");

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("Test.Culture.Invalid");
        result.Errors[0].Type.Should().Be(ErrorType.Invariant);
    }

    // ── EnsurePreferredThemeIsValid ──
    [Fact]
    public void EnsurePreferredThemeIsValid_WithNull_ReturnsSuccess()
    {
        Result result = CommonInvariants.EnsurePreferredThemeIsValid(
            null, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    [InlineData("LIGHT")]
    [InlineData("Dark")]
    public void EnsurePreferredThemeIsValid_WithKnownTheme_ReturnsSuccess(string theme)
    {
        Result result = CommonInvariants.EnsurePreferredThemeIsValid(
            theme, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("neon")]
    public void EnsurePreferredThemeIsValid_WithUnknownTheme_ReturnsFailure(string theme)
    {
        Result result = CommonInvariants.EnsurePreferredThemeIsValid(
            theme, "Test.Theme.Invalid", "Theme is not valid.", "SetPreferences", "Theme");

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("Test.Theme.Invalid");
        result.Errors[0].Type.Should().Be(ErrorType.Invariant);
    }

    [Fact]
    public void ThemeConstants_MatchTheAcceptedValues()
    {
        CommonInvariants.LightTheme.Should().Be("light");
        CommonInvariants.DarkTheme.Should().Be("dark");
    }
}

using AwesomeAssertions;
using MMCA.Common.Domain.Invariants;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Globalization;
using MMCA.Common.Shared.ValueObjects.Financial;

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

    // ── EnsureEnumIsDefined ──
    [Theory]
    [InlineData(TestScope.Event)]
    [InlineData(TestScope.Session)]
    public void EnsureEnumIsDefined_WithDeclaredMember_ReturnsSuccess(TestScope scope)
    {
        Result result = CommonInvariants.EnsureEnumIsDefined(
            scope, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void EnsureEnumIsDefined_WithUndeclaredValue_ReturnsFailure()
    {
        Result result = CommonInvariants.EnsureEnumIsDefined(
            (TestScope)99, "CheckIn.Scope.Invalid", "Check-in scope is not valid.", "Create", "scope");

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("CheckIn.Scope.Invalid");
        result.Errors[0].Message.Should().Be("Check-in scope is not valid.");
        result.Errors[0].Type.Should().Be(ErrorType.Invariant);
    }

    // ── EnsureEndIsNotBeforeStart ──
    [Fact]
    public void EnsureEndIsNotBeforeStart_WhenEndIsAfterStart_ReturnsSuccess()
    {
        Result result = CommonInvariants.EnsureEndIsNotBeforeStart(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 3), "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void EnsureEndIsNotBeforeStart_WhenEndEqualsStart_ReturnsSuccess()
    {
        Result result = CommonInvariants.EnsureEndIsNotBeforeStart(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue("a single-day range is legitimate; a strict check is a separate invariant");
    }

    [Fact]
    public void EnsureEndIsNotBeforeStart_WhenEndIsBeforeStart_ReturnsFailure()
    {
        Result result = CommonInvariants.EnsureEndIsNotBeforeStart(
            new DateTime(2026, 1, 3, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc),
            "Event.DateRange.Invalid",
            "Event end date must be on or after the start date.",
            "Create",
            "endDate");

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("Event.DateRange.Invalid");
    }

    // ── EnsureStringLengthIsWithin ──
    [Theory]
    [InlineData("a")]
    [InlineData("abcde")]
    public void EnsureStringLengthIsWithin_WhenWithinBounds_ReturnsSuccess(string value)
    {
        Result result = CommonInvariants.EnsureStringLengthIsWithin(
            value, 1, 5, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abcdef")]
    public void EnsureStringLengthIsWithin_WhenOutsideBounds_ReturnsFailure(string? value)
    {
        Result result = CommonInvariants.EnsureStringLengthIsWithin(
            value, 1, 5, "PointsEntry.SubjectKey.Invalid", "Subject key must be 1-5 characters.", "Create", "subjectKey");

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("PointsEntry.SubjectKey.Invalid");
        result.Errors[0].Type.Should().Be(ErrorType.Invariant);
    }

    [Fact]
    public void EnsureStringLengthIsWithin_WhenShorterThanMinimum_ReturnsFailure()
    {
        Result result = CommonInvariants.EnsureStringLengthIsWithin(
            "ab", 3, 5, "Test.TooShort", "Too short.", "Create", "Name");

        result.IsFailure.Should().BeTrue();
    }

    // ── EnsureOptionalStringMaxLength ──
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    public void EnsureOptionalStringMaxLength_WhenAbsentOrWithinLimit_ReturnsSuccess(string? value)
    {
        Result result = CommonInvariants.EnsureOptionalStringMaxLength(
            value, 10, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void EnsureOptionalStringMaxLength_WhenExceedsLimit_ReturnsFailure()
    {
        Result result = CommonInvariants.EnsureOptionalStringMaxLength(
            "this is too long", 5, "Activity.VenueUrl.TooLong", "Too long.", "Create", "venueUrl");

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("Activity.VenueUrl.TooLong");
    }

    // ── EnsureTimeZoneIsValid ──
    [Fact]
    public void EnsureTimeZoneIsValid_WhenNull_ReturnsSuccess()
    {
        Result result = CommonInvariants.EnsureTimeZoneIsValid(
            null, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void EnsureTimeZoneIsValid_WithRecognizedIdentifier_ReturnsSuccess()
    {
        Result result = CommonInvariants.EnsureTimeZoneIsValid(
            "UTC", "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Not/AZone")]
    public void EnsureTimeZoneIsValid_WithUnrecognizedIdentifier_ReturnsFailure(string timeZone)
    {
        Result result = CommonInvariants.EnsureTimeZoneIsValid(
            timeZone, "Event.TimeZone.Invalid", "The time zone is not recognized.", "Create", "timeZone");

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("Event.TimeZone.Invalid");
    }

    // ── EnsureUrlIsWellFormed ──
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com/logo.png")]
    [InlineData("http://example.com")]
    public void EnsureUrlIsWellFormed_WhenAbsentOrAbsoluteHttp_ReturnsSuccess(string? url)
    {
        Result result = CommonInvariants.EnsureUrlIsWellFormed(
            url, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("/relative/path")]
    [InlineData("example.com")]
    [InlineData("ftp://example.com/file")]
    public void EnsureUrlIsWellFormed_WhenNotAbsoluteHttp_ReturnsFailure(string url)
    {
        Result result = CommonInvariants.EnsureUrlIsWellFormed(
            url, "Sponsor.LogoUrl.Invalid", "Logo URL must be an absolute http or https URL.", "Create", "logoUrl");

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("Sponsor.LogoUrl.Invalid");
        result.Errors[0].Type.Should().Be(ErrorType.Invariant);
    }

    // ── EnsureCountIsWithin ──
    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(10)]
    public void EnsureCountIsWithin_WhenInRange_ReturnsSuccess(int count)
    {
        Result result = CommonInvariants.EnsureCountIsWithin(
            count, 2, 10, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    public void EnsureCountIsWithin_WhenOutOfRange_ReturnsFailure(int count)
    {
        Result result = CommonInvariants.EnsureCountIsWithin(
            count, 2, 10, "LivePoll.Options.CountInvalid", "A poll must have between 2 and 10 options.", "Create", "options");

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("LivePoll.Options.CountInvalid");
    }

    // ── EnsureCollectionIsEmpty ──
    [Fact]
    public void EnsureCollectionIsEmpty_WhenEmpty_ReturnsSuccess()
    {
        string[] empty = [];

        Result result = CommonInvariants.EnsureCollectionIsEmpty(
            empty, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void EnsureCollectionIsEmpty_WhenNull_ReturnsSuccess()
    {
        Result result = CommonInvariants.EnsureCollectionIsEmpty<string>(
            null, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void EnsureCollectionIsEmpty_WhenPopulated_ReturnsFailure()
    {
        string[] products = ["child"];

        Result result = CommonInvariants.EnsureCollectionIsEmpty(
            products, "Category.HasProducts", "Cannot delete a category that has products assigned to it.", "Delete", "products");

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("Category.HasProducts");
    }

    // ── EnsureValuesAreUnique ──
    [Fact]
    public void EnsureValuesAreUnique_WhenAllDistinct_ReturnsSuccess()
    {
        string[] values = ["one", "two"];

        Result result = CommonInvariants.EnsureValuesAreUnique(
            values, StringComparer.OrdinalIgnoreCase, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void EnsureValuesAreUnique_WhenNull_ReturnsSuccess()
    {
        Result result = CommonInvariants.EnsureValuesAreUnique<string>(
            null, null, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void EnsureValuesAreUnique_WhenDuplicateUnderComparer_ReturnsFailure()
    {
        string[] values = ["Yes", "yes"];

        Result result = CommonInvariants.EnsureValuesAreUnique(
            values, StringComparer.OrdinalIgnoreCase, "LivePoll.Options.Duplicate", "Option texts must be unique.", "Create", "options");

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("LivePoll.Options.Duplicate");
    }

    [Fact]
    public void EnsureValuesAreUnique_WithDefaultComparer_TreatsCaseAsDistinct()
    {
        string[] values = ["Yes", "yes"];

        Result result = CommonInvariants.EnsureValuesAreUnique(
            values, null, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue("a null comparer means the type's default equality, which is case-sensitive for strings");
    }

    // ── EnsureFlagIsTrue / EnsureFlagIsFalse ──
    [Fact]
    public void EnsureFlagIsTrue_WhenSet_ReturnsSuccess()
    {
        Result result = CommonInvariants.EnsureFlagIsTrue(
            true, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void EnsureFlagIsTrue_WhenClear_ReturnsFailure()
    {
        Result result = CommonInvariants.EnsureFlagIsTrue(
            false, "Event.NotPublished", "This action requires the event to be published.", "Publish", "isPublished");

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("Event.NotPublished");
    }

    [Fact]
    public void EnsureFlagIsFalse_WhenClear_ReturnsSuccess()
    {
        Result result = CommonInvariants.EnsureFlagIsFalse(
            false, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void EnsureFlagIsFalse_WhenSet_ReturnsFailure()
    {
        Result result = CommonInvariants.EnsureFlagIsFalse(
            true, "Session.IsServiceSession", "This action is not available for service sessions.", "Update", "isServiceSession");

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("Session.IsServiceSession");
    }

    // ── EnsureNullableIntIsPositive ──
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(500)]
    public void EnsureNullableIntIsPositive_WhenAbsentOrPositive_ReturnsSuccess(int? value)
    {
        Result result = CommonInvariants.EnsureNullableIntIsPositive(
            value, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void EnsureNullableIntIsPositive_WhenZeroOrNegative_ReturnsFailure(int value)
    {
        Result result = CommonInvariants.EnsureNullableIntIsPositive(
            value, "Room.Capacity.Invalid", "Room capacity must be a positive integer.", "Create", "capacity");

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("Room.Capacity.Invalid");
    }

    // ── EnsureIntIsNotNegative ──
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void EnsureIntIsNotNegative_WhenZeroOrPositive_ReturnsSuccess(int value)
    {
        Result result = CommonInvariants.EnsureIntIsNotNegative(
            value, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue("zero is what separates this from EnsureIntIsPositive");
    }

    [Fact]
    public void EnsureIntIsNotNegative_WhenNegative_ReturnsFailure()
    {
        Result result = CommonInvariants.EnsureIntIsNotNegative(
            -1, "Inventory.AvailableQuantity.Negative", "Available quantity cannot be negative.", "Create", "availableQuantity");

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("Inventory.AvailableQuantity.Negative");
    }
}

// ── Test helpers ──
public enum TestScope
{
    None = 0,
    Event = 1,
    Session = 2,
}

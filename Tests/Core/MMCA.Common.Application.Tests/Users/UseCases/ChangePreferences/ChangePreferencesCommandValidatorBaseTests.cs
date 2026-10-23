using AwesomeAssertions;
using FluentValidation.Results;
using FluentValidation.TestHelper;
using MMCA.Common.Application.Users;
using MMCA.Common.Application.Users.UseCases.ChangePreferences;
using MMCA.Common.Domain.Invariants;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Globalization;

namespace MMCA.Common.Application.Tests.Users.UseCases.ChangePreferences;

/// <summary>
/// Pins the shared preference-change validator (ADR-027 / ADR-028): a missing payload is one clear
/// failure, a null field means "leave unchanged", culture and theme are checked against the same
/// definitions the domain uses (<see cref="CommonInvariants"/>), and theme matching is case-insensitive.
/// </summary>
public sealed class ChangePreferencesCommandValidatorBaseTests
{
    private const string RequiredCode = "User.Preferences.Required";
    private const string RequiredMessage = "Preferences are required.";
    private const string CultureInvalidCode = "User.PreferredCulture.Invalid";
    private const string CultureInvalidMessage = "Culture is not supported.";
    private const string ThemeInvalidCode = "User.PreferredTheme.Invalid";
    private const string ThemeInvalidMessage = "Theme must be 'light' or 'dark'.";

    private readonly TestChangePreferencesCommandValidator _validator = new();

    /// <summary>Every culture on the framework allowlist.</summary>
    public static TheoryData<string> SupportedCultureValues => new(SupportedCultures.All);

    // ── Requirement 1: missing payload ──
    [Fact]
    public void Validate_WhenRequestNull_ReportsOnlyPreferencesRequired()
    {
        var command = new ValidatorTestCommand(1, null!);

        TestValidationResult<ValidatorTestCommand> result = _validator.TestValidate(command);

        result.IsValid.Should().BeFalse();
        ValidationFailure failure = result.Errors.Should().ContainSingle().Subject;
        failure.ErrorCode.Should().Be(RequiredCode);
        failure.ErrorMessage.Should().Be(RequiredMessage);
        result.Errors.Should().NotContain(e => e.ErrorCode == CultureInvalidCode || e.ErrorCode == ThemeInvalidCode);
    }

    // ── Requirement 2: null fields mean "leave unchanged" ──
    [Fact]
    public void Validate_WhenCultureAndThemeNull_NoErrors()
    {
        TestValidationResult<ValidatorTestCommand> result = Validate(culture: null, theme: null);

        result.ShouldNotHaveAnyValidationErrors();
    }

    // ── Requirement 3: culture ──
    [Theory]
    [MemberData(nameof(SupportedCultureValues))]
    public void Validate_WhenCultureSupported_NoErrors(string culture)
    {
        TestValidationResult<ValidatorTestCommand> result = Validate(culture, theme: null);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_WhenCultureUnsupported_ReportsCultureInvalid()
    {
        TestValidationResult<ValidatorTestCommand> result = Validate("xx-ZZ", theme: null);

        ValidationFailure failure = result.Errors.Should().ContainSingle().Subject;
        failure.ErrorCode.Should().Be(CultureInvalidCode);
        failure.ErrorMessage.Should().Be(CultureInvalidMessage);
    }

    // ── Requirement 4: theme (case-insensitive, ADR-028) ──
    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    [InlineData("Light")]
    [InlineData("DARK")]
    public void Validate_WhenThemeKnown_NoErrors(string theme)
    {
        TestValidationResult<ValidatorTestCommand> result = Validate(culture: null, theme);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("neon")]
    [InlineData("")]
    public void Validate_WhenThemeUnknown_ReportsThemeInvalid(string theme)
    {
        TestValidationResult<ValidatorTestCommand> result = Validate(culture: null, theme);

        ValidationFailure failure = result.Errors.Should().ContainSingle().Subject;
        failure.ErrorCode.Should().Be(ThemeInvalidCode);
        failure.ErrorMessage.Should().Be(ThemeInvalidMessage);
    }

    // ── Requirement 5: one definition of what is valid ──
    [Theory]
    [InlineData(null)]
    [InlineData("light")]
    [InlineData("dark")]
    [InlineData("DARK")]
    [InlineData("neon")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" light")]
    [InlineData("light ")]
    public void Validate_Theme_AgreesWithDomainInvariant(string? theme)
    {
        Result domain = CommonInvariants.EnsurePreferredThemeIsValid(
            theme, ThemeInvalidCode, ThemeInvalidMessage, "Test", "Theme");

        TestValidationResult<ValidatorTestCommand> result = Validate(culture: null, theme);

        result.IsValid.Should().Be(domain.IsSuccess);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("en-US")]
    [InlineData("es")]
    [InlineData("ES")]
    [InlineData("en-us")]
    [InlineData("es-MX")]
    [InlineData("qps-Ploc")]
    [InlineData("xx-ZZ")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" es")]
    public void Validate_Culture_AgreesWithDomainInvariant(string? culture)
    {
        Result domain = CommonInvariants.EnsurePreferredCultureIsValid(
            culture, CultureInvalidCode, CultureInvalidMessage, "Test", "Culture");

        TestValidationResult<ValidatorTestCommand> result = Validate(culture, theme: null);

        result.IsValid.Should().Be(domain.IsSuccess);
    }

    // ── Requirement 6: both invalid ──
    [Fact]
    public void Validate_WhenCultureAndThemeInvalid_ReportsBothFailures()
    {
        TestValidationResult<ValidatorTestCommand> result = Validate("xx-ZZ", "neon");

        result.Errors.Should().HaveCount(2);
        result.Errors.Should().ContainSingle(e => e.ErrorCode == CultureInvalidCode && e.ErrorMessage == CultureInvalidMessage);
        result.Errors.Should().ContainSingle(e => e.ErrorCode == ThemeInvalidCode && e.ErrorMessage == ThemeInvalidMessage);
    }

    private TestValidationResult<ValidatorTestCommand> Validate(string? culture, string? theme) =>
        _validator.TestValidate(new ValidatorTestCommand(1, new ChangePreferencesRequest(culture, theme)));

    /// <summary>App-side change-preferences command shape.</summary>
    private sealed record ValidatorTestCommand(UserIdentifierType UserId, ChangePreferencesRequest Request)
        : IUserScopedCommand<ChangePreferencesRequest>;

    /// <summary>App-side validator that adds nothing to the shared rules.</summary>
    private sealed class TestChangePreferencesCommandValidator : ChangePreferencesCommandValidatorBase<ValidatorTestCommand>;
}

using FluentValidation.TestHelper;
using MMCA.Common.Application.Auth.Validation;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Application.Tests.Auth.Validation;

public sealed class ResetPasswordRequestValidatorTests
{
    private const string ValidEmail = "test@example.com";
    private const string ValidToken = "abc123";
    private const string ValidPassword = "New-Password1!";

    private readonly ResetPasswordRequestValidator _validator = new();

    // ── Email ──
    [Fact]
    public void Validate_WhenEmailEmpty_HasValidationError()
    {
        TestValidationResult<ResetPasswordRequest> result =
            _validator.TestValidate(new ResetPasswordRequest(string.Empty, ValidToken, ValidPassword));

        result.ShouldHaveValidationErrorFor(x => x.Email)
            .WithErrorMessage("Email is required.");
    }

    [Fact]
    public void Validate_WhenEmailInvalid_HasValidationError()
    {
        TestValidationResult<ResetPasswordRequest> result =
            _validator.TestValidate(new ResetPasswordRequest("not-an-email", ValidToken, ValidPassword));

        result.ShouldHaveValidationErrorFor(x => x.Email)
            .WithErrorMessage("A valid email address is required.");
    }

    // ── Token ──
    [Fact]
    public void Validate_WhenTokenEmpty_HasValidationError()
    {
        TestValidationResult<ResetPasswordRequest> result =
            _validator.TestValidate(new ResetPasswordRequest(ValidEmail, string.Empty, ValidPassword));

        result.ShouldHaveValidationErrorFor(x => x.Token)
            .WithErrorMessage("Reset token is required.");
    }

    // ── New password complexity (the same rules registration and change-password enforce) ──
    [Theory]
    [InlineData("", "Password is required.")]
    [InlineData("Ab1!def", "Password must be at least 8 characters.")]
    [InlineData("abcdefg1!", "Password must contain at least one uppercase letter.")]
    [InlineData("ABCDEFG1!", "Password must contain at least one lowercase letter.")]
    [InlineData("Abcdefgh!", "Password must contain at least one digit.")]
    [InlineData("Abcdefg1", "Password must contain at least one special character.")]
    public void Validate_WhenNewPasswordWeak_HasValidationError(string password, string expectedMessage)
    {
        TestValidationResult<ResetPasswordRequest> result =
            _validator.TestValidate(new ResetPasswordRequest(ValidEmail, ValidToken, password));

        result.ShouldHaveValidationErrorFor(x => x.NewPassword)
            .WithErrorMessage(expectedMessage);
    }

    // ── Valid request ──
    [Fact]
    public void Validate_WhenValid_NoErrors()
    {
        TestValidationResult<ResetPasswordRequest> result =
            _validator.TestValidate(new ResetPasswordRequest(ValidEmail, ValidToken, ValidPassword));

        result.ShouldNotHaveAnyValidationErrors();
    }
}

using AwesomeAssertions;
using FluentValidation.TestHelper;
using MMCA.Common.Application.Auth.Validation;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Application.Tests.Auth.Validation;

public sealed class LoginRequestValidatorTests
{
    private readonly LoginRequestValidator _validator = new();

    // ── Email ──
    [Fact]
    public void Validate_WhenEmailEmpty_HasValidationError()
    {
        var request = new LoginRequest(string.Empty, "password123");

        TestValidationResult<LoginRequest> result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Email)
            .WithErrorMessage("Email is required.");
    }

    [Fact]
    public void Validate_WhenEmailInvalid_HasValidationError()
    {
        var request = new LoginRequest("not-an-email", "password123");

        TestValidationResult<LoginRequest> result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Email)
            .WithErrorMessage("A valid email address is required.");
    }

    // ── Password ──
    [Fact]
    public void Validate_WhenPasswordEmpty_HasValidationError()
    {
        var request = new LoginRequest("test@example.com", string.Empty);

        TestValidationResult<LoginRequest> result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Password)
            .WithErrorMessage("Password is required.");
    }

    // ── Valid request ──
    [Fact]
    public void Validate_WhenValid_NoErrors()
    {
        var request = new LoginRequest("test@example.com", "password123");

        TestValidationResult<LoginRequest> result = _validator.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }
}

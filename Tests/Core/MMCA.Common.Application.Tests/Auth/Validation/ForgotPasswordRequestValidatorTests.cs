using FluentValidation.TestHelper;
using MMCA.Common.Application.Auth.Validation;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Application.Tests.Auth.Validation;

public sealed class ForgotPasswordRequestValidatorTests
{
    private readonly ForgotPasswordRequestValidator _validator = new();

    // ── Email ──
    [Fact]
    public void Validate_WhenEmailEmpty_HasValidationError()
    {
        var request = new ForgotPasswordRequest(string.Empty);

        TestValidationResult<ForgotPasswordRequest> result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Email)
            .WithErrorMessage("Email is required.");
    }

    [Fact]
    public void Validate_WhenEmailInvalid_HasValidationError()
    {
        var request = new ForgotPasswordRequest("not-an-email");

        TestValidationResult<ForgotPasswordRequest> result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Email)
            .WithErrorMessage("A valid email address is required.");
    }

    // ── Valid request ──
    // An address with no account is deliberately valid: rejecting it here would be the enumeration
    // oracle the always-accepted response exists to close.
    [Fact]
    public void Validate_WhenValid_NoErrors()
    {
        var request = new ForgotPasswordRequest("test@example.com");

        TestValidationResult<ForgotPasswordRequest> result = _validator.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }
}

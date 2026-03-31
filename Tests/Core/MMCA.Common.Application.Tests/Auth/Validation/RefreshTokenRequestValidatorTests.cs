using AwesomeAssertions;
using FluentValidation.TestHelper;
using MMCA.Common.Application.Auth.Validation;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Application.Tests.Auth.Validation;

public sealed class RefreshTokenRequestValidatorTests
{
    private readonly RefreshTokenRequestValidator _validator = new();

    // ── AccessToken ──
    [Fact]
    public void Validate_WhenAccessTokenEmpty_HasValidationError()
    {
        var request = new RefreshTokenRequest(string.Empty, "valid-refresh-token");

        TestValidationResult<RefreshTokenRequest> result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.AccessToken)
            .WithErrorMessage("Access token is required.");
    }

    // ── RefreshToken ──
    [Fact]
    public void Validate_WhenRefreshTokenEmpty_HasValidationError()
    {
        var request = new RefreshTokenRequest("valid-access-token", string.Empty);

        TestValidationResult<RefreshTokenRequest> result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.RefreshToken)
            .WithErrorMessage("Refresh token is required.");
    }

    // ── Valid request ──
    [Fact]
    public void Validate_WhenValid_NoErrors()
    {
        var request = new RefreshTokenRequest("valid-access-token", "valid-refresh-token");

        TestValidationResult<RefreshTokenRequest> result = _validator.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }
}

using FluentValidation;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Application.Auth.Validation;

/// <summary>
/// Validates token refresh requests. Both the expired access token (for claim extraction)
/// and the refresh token (for rotation verification) are required.
/// </summary>
public class RefreshTokenRequestValidator : AbstractValidator<RefreshTokenRequest>
{
    public RefreshTokenRequestValidator()
    {
        RuleFor(x => x.AccessToken)
            .NotEmpty().WithMessage("Access token is required.");

        RuleFor(x => x.RefreshToken)
            .NotEmpty().WithMessage("Refresh token is required.");
    }
}

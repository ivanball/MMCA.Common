using FluentValidation;
using MMCA.Common.Application.Validation;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Application.Auth.Validation;

/// <summary>
/// Validates reset-password requests. The new password goes through the same
/// <see cref="StrongPasswordRules{T}"/> the registration and change-password requests use, so a
/// reset cannot be a way around the complexity policy.
/// </summary>
public class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("A valid email address is required.");

        RuleFor(x => x.Token)
            .NotEmpty().WithMessage("Reset token is required.");

        Include(new StrongPasswordRules<ResetPasswordRequest>(x => x.NewPassword));
    }
}

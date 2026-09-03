using FluentValidation;
using MMCA.Common.Shared.Auth.Requests;

namespace MMCA.Common.Application.Auth.Validation;

/// <summary>
/// Validates forgot-password requests. Only the address shape is checked: whether the address
/// belongs to an account is deliberately not a validation concern, because a 400 there would be the
/// enumeration oracle the always-accepted response exists to close.
/// </summary>
public class ForgotPasswordRequestValidator : AbstractValidator<ForgotPasswordRequest>
{
    public ForgotPasswordRequestValidator() =>
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("A valid email address is required.");
}

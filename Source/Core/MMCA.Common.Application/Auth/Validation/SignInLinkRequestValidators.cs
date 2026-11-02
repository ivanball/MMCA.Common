using FluentValidation;
using MMCA.Common.Shared.Auth.Requests;

namespace MMCA.Common.Application.Auth.Validation;

/// <summary>
/// Validates a request for a sign-in link. Only the address shape is checked: whether the address
/// belongs to an account is deliberately not a validation concern, because a 400 there would be the
/// enumeration oracle the always-accepted response exists to close.
/// </summary>
public class SendSignInLinkRequestValidator : AbstractValidator<SendSignInLinkRequest>
{
    /// <summary>Initializes the validator.</summary>
    public SendSignInLinkRequestValidator() =>
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("A valid email address is required.");
}

/// <summary>
/// Validates a sign-in-link redemption. Shape only: an unknown or expired token is the handler's
/// single generic rejection, not a 400 that would tell a caller their token was at least well formed.
/// </summary>
public class RedeemSignInLinkRequestValidator : AbstractValidator<RedeemSignInLinkRequest>
{
    /// <summary>Initializes the validator.</summary>
    public RedeemSignInLinkRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("A valid email address is required.");

        RuleFor(x => x.Token)
            .NotEmpty().WithMessage("A sign-in token is required.");
    }
}

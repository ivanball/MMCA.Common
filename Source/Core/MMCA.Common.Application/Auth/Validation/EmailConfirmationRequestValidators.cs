using FluentValidation;
using MMCA.Common.Shared.Auth.Requests;

namespace MMCA.Common.Application.Auth.Validation;

/// <summary>
/// Validates a request for a fresh confirmation email. Only the address shape is checked: whether the
/// address belongs to an account is deliberately not a validation concern, because a 400 there would
/// be the enumeration oracle the always-accepted response exists to close.
/// </summary>
public class SendEmailConfirmationRequestValidator : AbstractValidator<SendEmailConfirmationRequest>
{
    /// <summary>Initializes the validator.</summary>
    public SendEmailConfirmationRequestValidator() =>
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("A valid email address is required.");
}

/// <summary>
/// Validates a confirmation redemption. Shape only, matching
/// <see cref="ResetPasswordRequestValidator"/>: an unknown or expired token is the handler's single
/// generic rejection, not a 400 that would tell a caller their token was at least well formed.
/// </summary>
public class ConfirmEmailRequestValidator : AbstractValidator<ConfirmEmailRequest>
{
    /// <summary>Initializes the validator.</summary>
    public ConfirmEmailRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("A valid email address is required.");

        RuleFor(x => x.Token)
            .NotEmpty().WithMessage("A confirmation token is required.");
    }
}

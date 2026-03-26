using FluentValidation;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Application.Auth.Validation;

/// <summary>
/// Validates login requests. Intentionally minimal — only checks for non-empty email and password.
/// Detailed credential verification happens in the authentication service to avoid leaking
/// information about which field was wrong.
/// </summary>
public class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("A valid email address is required.");

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required.");
    }
}

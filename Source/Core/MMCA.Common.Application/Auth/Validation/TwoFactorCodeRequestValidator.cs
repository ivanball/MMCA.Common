using FluentValidation;
using MMCA.Common.Shared.Auth.Requests;

namespace MMCA.Common.Application.Auth.Validation;

/// <summary>
/// Validates the payload behind every code-proved two-factor action (confirm enrollment, disable,
/// regenerate recovery codes). Shape only: whether the code actually verifies is the handler's job,
/// because a validation failure and a verification failure must not be told apart by status code.
/// </summary>
/// <remarks>
/// The length ceiling is generous on purpose. It has to admit both a six-to-eight digit time-based
/// code and a recovery code, which is longer, so the rule exists to reject an obviously
/// non-credential payload rather than to characterize the code.
/// </remarks>
public class TwoFactorCodeRequestValidator : AbstractValidator<TwoFactorCodeRequest>
{
    /// <summary>The longest code the endpoint accepts before rejecting the payload outright.</summary>
    public const int MaxCodeLength = 64;

    /// <summary>Initializes the validator.</summary>
    public TwoFactorCodeRequestValidator() =>
        RuleFor(x => x.Code)
            .NotEmpty().WithMessage("A two-factor code is required.")
            .MaximumLength(MaxCodeLength).WithMessage("The two-factor code is not valid.");
}

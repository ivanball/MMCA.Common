using System.ComponentModel.DataAnnotations;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.UI.Pages.Auth;

/// <summary>
/// Client-side password-complexity rule for the Register form: at least 8 characters with an
/// uppercase, a lowercase, a digit, and a special (non-alphanumeric) character. It evaluates
/// <see cref="PasswordComplexity"/>, the same Unicode-aware definition the server's
/// <c>StrongPasswordRules</c> uses, so the EditForm gives the verdict the API would (rubric section 24
/// validation parity). Empty input is left to <see cref="RequiredAttribute"/> so the field shows one
/// clear message, not two.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class PasswordComplexityAttribute : ValidationAttribute
{
    public PasswordComplexityAttribute()
        : base("Password must be at least 8 characters and include uppercase, lowercase, a digit, and a special character.")
    {
    }

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is not string password || string.IsNullOrEmpty(password) || PasswordComplexity.Evaluate(password))
        {
            return ValidationResult.Success;
        }

        // The context is null when the attribute is called through the context-free IsValid(object)
        // overload, which the base class routes here; there is then no member to attach the error to.
        // FormatErrorMessage rather than ErrorMessage: it resolves an ErrorMessageResourceType /
        // ErrorMessageResourceName pair too (the auth models localize inside the attribute), and
        // returns a plain ErrorMessage or the default message unchanged.
        var members = validationContext?.MemberName is { } member ? new[] { member } : null;
        return new ValidationResult(FormatErrorMessage(validationContext?.DisplayName ?? string.Empty), members);
    }
}

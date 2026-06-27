using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.UI.Pages.Auth;

/// <summary>
/// Client-side password-complexity rule for the Register form: at least 8 characters with an
/// uppercase, a lowercase, a digit, and a special (non-alphanumeric) character. Mirrors the server's
/// rule so the EditForm gives the same verdict the API would (rubric §24 validation parity). Empty
/// input is left to <see cref="RequiredAttribute"/> so the field shows one clear message, not two.
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
        if (value is not string password || string.IsNullOrEmpty(password))
        {
            return ValidationResult.Success;
        }

        var isValid = password.Length >= 8
            && password.Any(char.IsUpper)
            && password.Any(char.IsLower)
            && password.Any(char.IsDigit)
            && password.Any(c => !char.IsLetterOrDigit(c));

        if (isValid)
        {
            return ValidationResult.Success;
        }

        var members = validationContext.MemberName is { } member ? new[] { member } : null;
        return new ValidationResult(ErrorMessage, members);
    }
}

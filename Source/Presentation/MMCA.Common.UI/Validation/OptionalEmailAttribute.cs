using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.UI.Validation;

/// <summary>
/// Client-side rule for an OPTIONAL email address: a blank value passes, a non-blank one must be a
/// valid address by the same rule the server applies (<c>EmailRules</c> in MMCA.Common.Application,
/// whose FluentValidation <c>EmailAddress()</c> check accepts a value with exactly one <c>@</c> that
/// is neither the first nor the last character), so a form gives the same verdict the API would
/// (rubric section 24 validation parity).
/// <para>
/// Null, empty and whitespace pass. The stock <see cref="EmailAddressAttribute"/> cannot express an
/// optional field that the user may clear back to blank, and pairing this with
/// <see cref="RequiredAttribute"/> is the caller's decision when the field is mandatory.
/// </para>
/// <para>
/// <see cref="ValidationAttribute.ErrorMessage"/> is emitted unchanged rather than formatted, which
/// is what lets a model declare a localization resource key
/// (<c>ErrorMessage = "Auth.Field.Email.Invalid"</c>): <c>DataAnnotationsModelValidator</c> resolves
/// every message it receives against the page's <c>IStringLocalizer</c> and passes an unknown key
/// through untouched, so a plain-English message renders exactly as written.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class OptionalEmailAttribute : ValidationAttribute
{
    /// <summary>Creates the rule with its default English message.</summary>
    public OptionalEmailAttribute()
        : base("Enter a valid email address.")
    {
    }

    /// <inheritdoc />
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        ArgumentNullException.ThrowIfNull(validationContext);

        if (value is not string email || string.IsNullOrWhiteSpace(email))
        {
            return ValidationResult.Success;
        }

        if (IsValidEmailFormat(email))
        {
            return ValidationResult.Success;
        }

        string[]? members = validationContext.MemberName is { } member ? [member] : null;
        return new ValidationResult(ErrorMessage, members);
    }

    /// <summary>
    /// Returns whether <paramref name="value"/> passes the server's email format rule: exactly one
    /// <c>@</c>, with at least one character on each side of it. Blank values are not judged here;
    /// <see cref="IsValid(object?, ValidationContext)"/> lets them through before calling this.
    /// </summary>
    /// <param name="value">The non-blank candidate address.</param>
    /// <returns><see langword="true"/> when the format is acceptable.</returns>
    private static bool IsValidEmailFormat(string value)
    {
        var index = value.IndexOf('@', StringComparison.Ordinal);
        return index > 0
            && index != value.Length - 1
            && index == value.LastIndexOf('@');
    }
}

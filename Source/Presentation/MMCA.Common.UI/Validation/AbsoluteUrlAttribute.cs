using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.UI.Validation;

/// <summary>
/// Client-side rule requiring an absolute <c>http</c> or <c>https</c> URL. It mirrors the server's
/// <c>AbsoluteUrlRules</c> (MMCA.Common.Application) so a form gives the same verdict the API would
/// (rubric §24 validation parity), which matters more here than for most rules: these values are
/// rendered straight into an image source or a link target, so accepting <c>javascript:</c> or
/// <c>data:</c> on the client and rejecting it on the server means the only thing standing between a
/// pasted script URL and the page is a round trip.
/// <para>
/// Null, empty and whitespace pass. Optionality is the caller's decision, expressed by pairing this
/// with <see cref="RequiredAttribute"/> when the field is mandatory, so a blank required field shows
/// one clear message instead of two.
/// </para>
/// <para>
/// <see cref="ValidationAttribute.ErrorMessage"/> is emitted unchanged rather than formatted, which
/// is what lets a model declare a localization resource key
/// (<c>ErrorMessage = "Validation.AbsoluteUrl"</c>): <c>DataAnnotationsModelValidator</c> resolves
/// every message it receives against the page's <c>IStringLocalizer</c> and passes an unknown key
/// through untouched, so a plain-English message renders exactly as written.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class AbsoluteUrlAttribute : ValidationAttribute
{
    /// <summary>Creates the rule with its default English message.</summary>
    public AbsoluteUrlAttribute()
        : base("The value must be an absolute http or https URL.")
    {
    }

    /// <inheritdoc />
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        ArgumentNullException.ThrowIfNull(validationContext);

        if (value is not string url || string.IsNullOrWhiteSpace(url))
        {
            return ValidationResult.Success;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)))
        {
            return ValidationResult.Success;
        }

        string[]? members = validationContext.MemberName is { } member ? [member] : null;
        return new ValidationResult(ErrorMessage, members);
    }
}

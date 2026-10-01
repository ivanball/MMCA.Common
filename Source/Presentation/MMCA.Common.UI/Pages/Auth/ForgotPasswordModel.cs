using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.UI.Pages.Auth;

/// <summary>
/// EditForm model for the Forgot Password page. Only the address is collected; the server decides
/// (silently) whether an account exists, so client validation is limited to shape (rubric §24).
/// <para>
/// Each <c>ErrorMessage</c> is a resource key, resolved by the page's localizing
/// <see cref="MMCA.Common.UI.Validation.LocalizedDataAnnotationsValidator"/> (ADR-027).
/// </para>
/// </summary>
public sealed class ForgotPasswordModel
{
    [Required(ErrorMessage = "Auth.Field.Email.Required")]
    [EmailAddress(ErrorMessage = "Auth.Field.Email.Invalid")]
    public string Email { get; set; } = string.Empty;
}

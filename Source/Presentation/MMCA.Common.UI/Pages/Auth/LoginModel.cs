using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.UI.Pages.Auth;

/// <summary>
/// EditForm model for the Login page. Field-level Required/email validation (rubric §24); the server
/// remains the authority on whether the credentials are actually valid.
/// <para>
/// Each <c>ErrorMessage</c> is a resource key, resolved by the page's localizing
/// <see cref="MMCA.Common.UI.Validation.LocalizedDataAnnotationsValidator"/> (ADR-027).
/// </para>
/// </summary>
public sealed class LoginModel
{
    [Required(ErrorMessage = "Auth.Field.Email.Required")]
    [EmailAddress(ErrorMessage = "Auth.Field.Email.Invalid")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Auth.Field.Password.Required")]
    public string Password { get; set; } = string.Empty;
}

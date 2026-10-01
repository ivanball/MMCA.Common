using System.ComponentModel.DataAnnotations;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.UI.Pages.Auth;

/// <summary>
/// EditForm model for the Reset Password page. Email and token arrive prefilled from the reset link
/// but stay editable so a user who only has the raw token from the email can type it in (no deep-link
/// support needed on native heads). The complexity rule mirrors the server's (rubric §24).
/// <para>
/// Each <c>ErrorMessage</c> is a resource key, resolved by the page's localizing
/// <see cref="MMCA.Common.UI.Validation.LocalizedDataAnnotationsValidator"/> (ADR-027).
/// </para>
/// </summary>
public sealed class ResetPasswordModel
{
    [Required(ErrorMessage = "Auth.Field.Email.Required")]
    [EmailAddress(ErrorMessage = "Auth.Field.Email.Invalid")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Auth.Field.Token.Required")]
    public string Token { get; set; } = string.Empty;

    [Required(ErrorMessage = "Auth.Field.Password.Required")]
    [StringLength(PasswordComplexity.MaximumLength, ErrorMessage = "Auth.Field.Password.MaxLength")]
    [PasswordComplexity(ErrorMessage = "Auth.Field.Password.Complexity")]
    public string NewPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "Auth.Field.ConfirmPassword.Required")]
    [Compare(nameof(NewPassword), ErrorMessage = "Auth.Field.ConfirmPassword.Mismatch")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

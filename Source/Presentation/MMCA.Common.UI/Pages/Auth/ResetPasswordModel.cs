using System.ComponentModel.DataAnnotations;
using MMCA.Common.Shared.Auth;
using MMCA.Common.UI.Resources;

namespace MMCA.Common.UI.Pages.Auth;

/// <summary>
/// EditForm model for the Reset Password page. Email and token arrive prefilled from the reset link
/// but stay editable so a user who only has the raw token from the email can type it in (no deep-link
/// support needed on native heads). The complexity rule mirrors the server's (rubric §24).
/// <para>
/// Each message is read from the shared resources through <see cref="AuthFieldMessages"/> (ADR-027),
/// inside the attribute, so the form's validator and MudBlazor's own field-level pass (which runs the
/// <c>For</c> property's attributes when a field is touched) both show the localized text, never a key.
/// </para>
/// </summary>
public sealed class ResetPasswordModel
{
    [Required(ErrorMessageResourceType = typeof(AuthFieldMessages), ErrorMessageResourceName = nameof(AuthFieldMessages.EmailRequired))]
    [EmailAddress(ErrorMessageResourceType = typeof(AuthFieldMessages), ErrorMessageResourceName = nameof(AuthFieldMessages.EmailInvalid))]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessageResourceType = typeof(AuthFieldMessages), ErrorMessageResourceName = nameof(AuthFieldMessages.TokenRequired))]
    public string Token { get; set; } = string.Empty;

    [Required(ErrorMessageResourceType = typeof(AuthFieldMessages), ErrorMessageResourceName = nameof(AuthFieldMessages.PasswordRequired))]
    [StringLength(PasswordComplexity.MaximumLength, ErrorMessageResourceType = typeof(AuthFieldMessages), ErrorMessageResourceName = nameof(AuthFieldMessages.PasswordMaxLength))]
    [PasswordComplexity(ErrorMessageResourceType = typeof(AuthFieldMessages), ErrorMessageResourceName = nameof(AuthFieldMessages.PasswordComplexity))]
    public string NewPassword { get; set; } = string.Empty;

    [Required(ErrorMessageResourceType = typeof(AuthFieldMessages), ErrorMessageResourceName = nameof(AuthFieldMessages.ConfirmPasswordRequired))]
    [Compare(nameof(NewPassword), ErrorMessageResourceType = typeof(AuthFieldMessages), ErrorMessageResourceName = nameof(AuthFieldMessages.ConfirmPasswordMismatch))]
    public string ConfirmPassword { get; set; } = string.Empty;
}

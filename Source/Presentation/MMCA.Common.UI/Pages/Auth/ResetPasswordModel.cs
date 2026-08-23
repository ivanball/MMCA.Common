using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.UI.Pages.Auth;

/// <summary>
/// EditForm model for the Reset Password page. Email and token arrive prefilled from the reset link
/// but stay editable so a user who only has the raw token from the email can type it in (no deep-link
/// support needed on native heads). The complexity rule mirrors the server's (rubric §24).
/// </summary>
public sealed class ResetPasswordModel
{
    [Required(ErrorMessage = "Email is required")]
    [EmailAddress(ErrorMessage = "Enter a valid email address")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Reset token is required")]
    public string Token { get; set; } = string.Empty;

    [Required(ErrorMessage = "Password is required")]
    [PasswordComplexity]
    public string NewPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "Please confirm your password")]
    [Compare(nameof(NewPassword), ErrorMessage = "Passwords do not match")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.UI.Pages.Auth;

/// <summary>
/// EditForm model for the Forgot Password page. Only the address is collected; the server decides
/// (silently) whether an account exists, so client validation is limited to shape (rubric §24).
/// </summary>
public sealed class ForgotPasswordModel
{
    [Required(ErrorMessage = "Email is required")]
    [EmailAddress(ErrorMessage = "Enter a valid email address")]
    public string Email { get; set; } = string.Empty;
}

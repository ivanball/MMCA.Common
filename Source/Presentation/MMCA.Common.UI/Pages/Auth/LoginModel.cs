using System.ComponentModel.DataAnnotations;
using MMCA.Common.UI.Resources;

namespace MMCA.Common.UI.Pages.Auth;

/// <summary>
/// EditForm model for the Login page. Field-level Required/email validation (rubric §24); the server
/// remains the authority on whether the credentials are actually valid.
/// <para>
/// Each message is read from the shared resources through <see cref="AuthFieldMessages"/> (ADR-027),
/// inside the attribute, so the form's validator and MudBlazor's own field-level pass (which runs the
/// <c>For</c> property's attributes when a field is touched) both show the localized text, never a key.
/// </para>
/// </summary>
public sealed class LoginModel
{
    [Required(ErrorMessageResourceType = typeof(AuthFieldMessages), ErrorMessageResourceName = nameof(AuthFieldMessages.EmailRequired))]
    [EmailAddress(ErrorMessageResourceType = typeof(AuthFieldMessages), ErrorMessageResourceName = nameof(AuthFieldMessages.EmailInvalid))]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessageResourceType = typeof(AuthFieldMessages), ErrorMessageResourceName = nameof(AuthFieldMessages.PasswordRequired))]
    public string Password { get; set; } = string.Empty;
}

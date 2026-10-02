using System.ComponentModel.DataAnnotations;
using MMCA.Common.UI.Resources;

namespace MMCA.Common.UI.Pages.Auth;

/// <summary>
/// EditForm model for the Forgot Password page. Only the address is collected; the server decides
/// (silently) whether an account exists, so client validation is limited to shape (rubric §24).
/// <para>
/// Each message is read from the shared resources through <see cref="AuthFieldMessages"/> (ADR-027),
/// inside the attribute, so the form's validator and MudBlazor's own field-level pass (which runs the
/// <c>For</c> property's attributes when a field is touched) both show the localized text, never a key.
/// </para>
/// </summary>
public sealed class ForgotPasswordModel
{
    [Required(ErrorMessageResourceType = typeof(AuthFieldMessages), ErrorMessageResourceName = nameof(AuthFieldMessages.EmailRequired))]
    [EmailAddress(ErrorMessageResourceType = typeof(AuthFieldMessages), ErrorMessageResourceName = nameof(AuthFieldMessages.EmailInvalid))]
    public string Email { get; set; } = string.Empty;
}

using System.ComponentModel.DataAnnotations;
using MMCA.Common.Shared.Auth;
using MMCA.Common.UI.Resources;

namespace MMCA.Common.UI.Pages.Auth;

/// <summary>
/// EditForm model for the Register page. DataAnnotations drive field-level validation messages tied
/// to each input (rubric §24), and mirror the server's rules so client and server agree.
/// <para>
/// Each message is read from the shared resources through <see cref="AuthFieldMessages"/> (ADR-027),
/// inside the attribute, so the form's validator and MudBlazor's own field-level pass (which runs the
/// <c>For</c> property's attributes when a field is touched) both show the localized text, never a key.
/// </para>
/// </summary>
public sealed class RegisterModel
{
    [Required(ErrorMessageResourceType = typeof(AuthFieldMessages), ErrorMessageResourceName = nameof(AuthFieldMessages.FirstNameRequired))]
    public string FirstName { get; set; } = string.Empty;

    [Required(ErrorMessageResourceType = typeof(AuthFieldMessages), ErrorMessageResourceName = nameof(AuthFieldMessages.LastNameRequired))]
    public string LastName { get; set; } = string.Empty;

    [Required(ErrorMessageResourceType = typeof(AuthFieldMessages), ErrorMessageResourceName = nameof(AuthFieldMessages.EmailRequired))]
    [EmailAddress(ErrorMessageResourceType = typeof(AuthFieldMessages), ErrorMessageResourceName = nameof(AuthFieldMessages.EmailInvalid))]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessageResourceType = typeof(AuthFieldMessages), ErrorMessageResourceName = nameof(AuthFieldMessages.PasswordRequired))]
    [StringLength(PasswordComplexity.MaximumLength, ErrorMessageResourceType = typeof(AuthFieldMessages), ErrorMessageResourceName = nameof(AuthFieldMessages.PasswordMaxLength))]
    [PasswordComplexity(ErrorMessageResourceType = typeof(AuthFieldMessages), ErrorMessageResourceName = nameof(AuthFieldMessages.PasswordComplexity))]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessageResourceType = typeof(AuthFieldMessages), ErrorMessageResourceName = nameof(AuthFieldMessages.ConfirmPasswordRequired))]
    [Compare(nameof(Password), ErrorMessageResourceType = typeof(AuthFieldMessages), ErrorMessageResourceName = nameof(AuthFieldMessages.ConfirmPasswordMismatch))]
    public string ConfirmPassword { get; set; } = string.Empty;

    // Address is optional — no validation attributes; an empty Line 1 means "no address supplied".
    public string AddressLine1 { get; set; } = string.Empty;
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? ZipCode { get; set; }
    public string? Country { get; set; }

    // The "I agree to the Terms" box. No validation attribute: it is required only when the host
    // configures a Terms URL (LegalSettings.TermsUrl), and the page enforces that by keeping the
    // submit button disabled until it is ticked.
    public bool AcceptedTerms { get; set; }
}

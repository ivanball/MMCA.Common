using System.ComponentModel.DataAnnotations;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.UI.Pages.Auth;

/// <summary>
/// EditForm model for the Register page. DataAnnotations drive field-level validation messages tied
/// to each input (rubric §24), and mirror the server's rules so client and server agree.
/// <para>
/// Each <c>ErrorMessage</c> is a resource key, resolved by the page's localizing
/// <see cref="MMCA.Common.UI.Validation.LocalizedDataAnnotationsValidator"/> (ADR-027).
/// </para>
/// </summary>
public sealed class RegisterModel
{
    [Required(ErrorMessage = "Auth.Field.FirstName.Required")]
    public string FirstName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Auth.Field.LastName.Required")]
    public string LastName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Auth.Field.Email.Required")]
    [EmailAddress(ErrorMessage = "Auth.Field.Email.Invalid")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Auth.Field.Password.Required")]
    [StringLength(PasswordComplexity.MaximumLength, ErrorMessage = "Auth.Field.Password.MaxLength")]
    [PasswordComplexity(ErrorMessage = "Auth.Field.Password.Complexity")]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "Auth.Field.ConfirmPassword.Required")]
    [Compare(nameof(Password), ErrorMessage = "Auth.Field.ConfirmPassword.Mismatch")]
    public string ConfirmPassword { get; set; } = string.Empty;

    // Address is optional — no validation attributes; an empty Line 1 means "no address supplied".
    public string AddressLine1 { get; set; } = string.Empty;
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? ZipCode { get; set; }
    public string? Country { get; set; }
}

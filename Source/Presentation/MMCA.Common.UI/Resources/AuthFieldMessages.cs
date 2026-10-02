using System.Globalization;
using System.Resources;

namespace MMCA.Common.UI.Resources;

/// <summary>
/// The auth form models' DataAnnotations messages, read from <see cref="SharedResource"/>'s
/// <c>.resx</c> in the current UI culture, for use as
/// <see cref="System.ComponentModel.DataAnnotations.ValidationAttribute.ErrorMessageResourceType"/>.
/// </summary>
/// <remarks>
/// A plain <c>ErrorMessage = "Auth.Field.Email.Required"</c> key is only translated by the form's
/// localizing validator. MudBlazor (9.11) also runs the <c>For</c> property's attributes itself when a
/// field is touched and shows the attribute's message verbatim, so a key there reached the screen raw.
/// Resolving the message inside the attribute localizes every path that asks for it. The keys stay
/// the same resource entries the rest of the UI uses (ADR-027), and a missing entry falls back to the
/// key, exactly like <c>IStringLocalizer</c>.
/// </remarks>
internal static class AuthFieldMessages
{
    private static readonly ResourceManager Resources =
        new(typeof(SharedResource).FullName!, typeof(SharedResource).Assembly);

    internal static string FirstNameRequired => Get("Auth.Field.FirstName.Required");

    internal static string LastNameRequired => Get("Auth.Field.LastName.Required");

    internal static string EmailRequired => Get("Auth.Field.Email.Required");

    internal static string EmailInvalid => Get("Auth.Field.Email.Invalid");

    internal static string PasswordRequired => Get("Auth.Field.Password.Required");

    internal static string PasswordMaxLength => Get("Auth.Field.Password.MaxLength");

    internal static string PasswordComplexity => Get("Auth.Field.Password.Complexity");

    internal static string ConfirmPasswordRequired => Get("Auth.Field.ConfirmPassword.Required");

    internal static string ConfirmPasswordMismatch => Get("Auth.Field.ConfirmPassword.Mismatch");

    internal static string TokenRequired => Get("Auth.Field.Token.Required");

    private static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;
}

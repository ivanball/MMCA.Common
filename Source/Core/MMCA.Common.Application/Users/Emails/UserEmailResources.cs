using System.Globalization;
using System.Resources;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Globalization;

namespace MMCA.Common.Application.Users.Emails;

/// <summary>
/// Text of the account emails the framework sends (password reset, email confirmation), localized
/// per recipient (ADR-027). The <c>.resx</c> siblings (<c>UserEmailResources.resx</c> /
/// <c>UserEmailResources.es.resx</c>) are read through a plain <see cref="ResourceManager"/>, the
/// localization pattern available at this layer: the Application layer references no
/// <c>Microsoft.Extensions.Localization</c> and may not reference the Presentation-layer helpers.
/// </summary>
internal static class UserEmailResources
{
    private static readonly ResourceManager Resources = new(typeof(UserEmailResources));

    /// <summary>
    /// Runs <paramref name="compose"/> with the current UI culture set to the recipient's stored
    /// preference (<see cref="IUserPreferences.PreferredCulture"/>, resolved to the closest supported
    /// culture), so the default text and any override that reads the current UI culture speak the
    /// recipient's language. Without a stored preference the request's culture stays in force.
    /// </summary>
    /// <typeparam name="T">The composed result.</typeparam>
    /// <param name="recipient">The resolved account the email goes to.</param>
    /// <param name="compose">Builds the subject and body.</param>
    /// <returns>What <paramref name="compose"/> returned.</returns>
    public static T InRecipientCulture<T>(object recipient, Func<T> compose)
    {
        var previous = CultureInfo.CurrentUICulture;
        if (recipient is IUserPreferences { PreferredCulture: { } preferred } && !string.IsNullOrWhiteSpace(preferred))
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(SupportedCultures.ResolveClosest(preferred));
        }

        try
        {
            return compose();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    /// <summary>The resource <paramref name="key"/> in the current UI culture (English when untranslated).</summary>
    /// <param name="key">The resource key, e.g. <c>ForgotPassword.Subject</c>.</param>
    /// <returns>The localized text.</returns>
    public static string Get(string key) =>
        Resources.GetString(key, CultureInfo.CurrentUICulture)
        ?? throw new InvalidOperationException($"The account email resource '{key}' is missing.");

    /// <summary>
    /// The plural form of <paramref name="key"/> for <paramref name="count"/> in the current UI culture,
    /// formatted with <paramref name="args"/>: <c>{key}.One</c> when the count is exactly one and
    /// <c>{key}.Other</c> otherwise, the same one/other split <c>StringLocalizerPluralExtensions</c>
    /// applies to the UI resources (ADR-027).
    /// </summary>
    /// <param name="key">The base resource key, without a plural suffix.</param>
    /// <param name="count">The quantity that selects the plural form.</param>
    /// <param name="args">The values for the format placeholders.</param>
    /// <returns>The localized, formatted text.</returns>
    public static string Plural(string key, int count, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, Get(key + (count == 1 ? ".One" : ".Other")), args);
}

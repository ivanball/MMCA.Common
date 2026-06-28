using System.Globalization;
using Microsoft.Extensions.Localization;

namespace MMCA.Common.UI.Pages.Common;

/// <summary>
/// Consistent user-facing notification messages used by all page code-behinds.
/// Centralizes message formatting so snackbar messages are uniform across the application.
/// <para>
/// Localized (ADR-027): when a shared localizer is configured (via <see cref="Configure"/>, called once
/// from the root layout) each message resolves from the <c>SharedResource</c> <c>.resx</c> by key against
/// the current UI culture; until then — or for an unknown key — the English format string is the fallback.
/// The static signatures are unchanged so existing call sites do not move.
/// </para>
/// </summary>
public static class ErrorMessages
{
    private static IStringLocalizer? _localizer;

    /// <summary>
    /// Wires the shared <see cref="IStringLocalizer"/> used to localize these messages. Called once from
    /// the root layout's initialization; idempotent. Until set, the English fallbacks are returned.
    /// </summary>
    /// <param name="localizer">The <c>IStringLocalizer&lt;SharedResource&gt;</c> instance.</param>
    public static void Configure(IStringLocalizer localizer) => _localizer = localizer;

    private static string Localize(string key, string fallbackFormat, params object[] args)
    {
        if (_localizer is not null)
        {
            LocalizedString localized = _localizer[key, args];
            if (!localized.ResourceNotFound)
            {
                return localized.Value;
            }
        }

        return string.Format(CultureInfo.CurrentCulture, fallbackFormat, args);
    }

    public static string LoadError(string entityName, Exception ex) =>
        Localize("Common.Error.Load", "Error loading {0}. {1}", entityName, ex.Message);

    public static string SaveError(string entityName, Exception ex) =>
        Localize("Common.Error.Save", "Error saving {0}. {1}", entityName, ex.Message);

    public static string DeleteError(string entityName, Exception ex) =>
        Localize("Common.Error.Delete", "Error deleting {0}. {1}", entityName, ex.Message);

    public static string DeleteFailed(string entityName) =>
        Localize("Common.Error.DeleteFailed", "Failed to delete the {0}.", entityName);

    public static string NotFound(string entityName, object id) =>
        Localize("Common.Error.NotFound", "{0} with Id {1} was not found.", entityName, id);

    public static string ValidationError =>
        Localize("Common.Error.Validation", "There were validation errors. Please check the form.");

    public static string Success(string entityName, string action) =>
        Localize("Common.Success", "{0} {1} successfully.", entityName, action);
}

using System.Globalization;
using Microsoft.Extensions.Localization;

namespace MMCA.Common.UI.Pages.Common;

/// <summary>
/// Consistent user-facing notification messages used by all page code-behinds.
/// Centralizes message formatting so snackbar messages are uniform across the application.
/// <para>
/// Localized (ADR-027): when a shared localizer is configured (via <see cref="Configure"/>, called once
/// from the root layout) each message resolves from the <c>SharedResource</c> <c>.resx</c> by key against
/// the current UI culture; until then, or for an unknown key, the English format string is the fallback.
/// </para>
/// <para>
/// <b>Scope.</b> A server answer reaches a page as a <c>Result</c> whose errors carry the API's own
/// text, rendered through <c>MMCA.Common.UI.Common.ResultUiExtensions</c> (<c>NotifyOnFailure</c>,
/// <c>OnFailureSetError</c>). These helpers cover the exceptions a page can still see, which are its
/// own faults rather than the server's: a JS-interop failure, a mapping bug, a callback a page
/// supplied. Such an exception's <c>Message</c> is never shown: raw exception text is neither
/// localizable nor safe to surface (ADR-027 Decision 9, rubric §24), so the message is always the
/// localized template.
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

    /// <summary>
    /// Load-failure message. Pass a LOCALIZED entity name (e.g. the page's localized <c>Title</c> or an
    /// <c>L["Entity.X"]</c> value). The exception's own <c>Message</c> is passed to the resource as the
    /// second format argument, which the shipped templates deliberately ignore: raw exception text is
    /// neither localizable nor safe to surface (ADR-027 Decision 9 / rubric §24), so the user always
    /// sees the entity-noun template.
    /// </summary>
    public static string LoadError(string entityName, Exception ex) =>
        Localize("Common.Error.Load", "Error loading {0}.", entityName, ex.Message);

    /// <inheritdoc cref="LoadError"/>
    public static string SaveError(string entityName, Exception ex) =>
        Localize("Common.Error.Save", "Error saving {0}.", entityName, ex.Message);

    /// <inheritdoc cref="LoadError"/>
    public static string DeleteError(string entityName, Exception ex) =>
        Localize("Common.Error.Delete", "Error deleting {0}.", entityName, ex.Message);

    public static string DeleteFailed(string entityName) =>
        Localize("Common.Error.DeleteFailed", "Failed to delete the {0}.", entityName);

    public static string NotFound(string entityName, object id) =>
        Localize("Common.Error.NotFound", "{0} with Id {1} was not found.", entityName, id);

    public static string ValidationError =>
        Localize("Common.Error.Validation", "There were validation errors. Please check the form.");
}

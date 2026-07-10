using System.Globalization;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Exceptions;

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

    /// <summary>
    /// Load-failure message. Pass a LOCALIZED entity name (e.g. the page's localized <c>Title</c> or an
    /// <c>L["Entity.X"]</c> value). A <see cref="DomainInvariantViolationException"/> is the one exception
    /// whose <c>Message</c> IS shown, replacing the template: <c>ServiceExceptionHelper</c> rethrows the
    /// API's Problem Details errors as that type, and their text is curated domain wording already
    /// localized server-side to the request culture (ADR-027 Decisions 3 and 5), so the user sees the
    /// actual business rule that rejected the action. Every other exception's <c>Message</c> is
    /// deliberately NOT shown (raw exception text is neither localizable nor safe to surface, ADR-027
    /// Decision 9 / rubric §24).
    /// </summary>
    public static string LoadError(string entityName, Exception ex) =>
        ex is DomainInvariantViolationException
            ? ex.Message
            : Localize("Common.Error.Load", "Error loading {0}.", entityName, ex.Message);

    /// <inheritdoc cref="LoadError"/>
    public static string SaveError(string entityName, Exception ex) =>
        ex is DomainInvariantViolationException
            ? ex.Message
            : Localize("Common.Error.Save", "Error saving {0}.", entityName, ex.Message);

    /// <inheritdoc cref="LoadError"/>
    public static string DeleteError(string entityName, Exception ex) =>
        ex is DomainInvariantViolationException
            ? ex.Message
            : Localize("Common.Error.Delete", "Error deleting {0}.", entityName, ex.Message);

    /// <summary>
    /// Message for a failed user action reported via snackbar, for pages whose fallback is a
    /// whole-sentence key of their own resource pair rather than an entity-noun template. A
    /// <see cref="DomainInvariantViolationException"/> shows its <c>Message</c> verbatim (see
    /// <see cref="LoadError"/> for why that is safe); any other exception yields
    /// <paramref name="localizedFallback"/>, which the caller must have localized already
    /// (e.g. <c>L["Snackbar.ActionFailed"].Value</c>).
    /// </summary>
    public static string ActionError(Exception ex, string localizedFallback) =>
        ex is DomainInvariantViolationException ? ex.Message : localizedFallback;

    public static string DeleteFailed(string entityName) =>
        Localize("Common.Error.DeleteFailed", "Failed to delete the {0}.", entityName);

    public static string NotFound(string entityName, object id) =>
        Localize("Common.Error.NotFound", "{0} with Id {1} was not found.", entityName, id);

    public static string ValidationError =>
        Localize("Common.Error.Validation", "There were validation errors. Please check the form.");

    /// <summary>
    /// Composes "{0} {1} successfully." from an entity noun and an English verb fragment. Obsolete
    /// because composed sentences cannot be translated correctly (Spanish gender/word agreement breaks:
    /// "creado" vs "creada" depends on the entity) — a rubric §27 red flag. Use a whole-sentence key in
    /// the page's own resource pair instead, e.g. <c>Snackbar.Add(L["Snackbar.Created"], ...)</c> with
    /// <c>Snackbar.Created</c> = "Event created successfully." / "Evento creado correctamente." (ADR-027).
    /// </summary>
#pragma warning disable S1133 // Deprecated code should be removed: the obsoletion IS the migration mechanism — it turns every remaining consumer call site into a build error (TreatWarningsAsErrors) during the lockstep sweep; the member is removed once all consumers are swept.
    [Obsolete("Composed sentences cannot be translated (ADR-027). Use a whole-sentence key in the page's own resource pair, e.g. Snackbar.Add(L[\"Snackbar.Created\"], ...).")]
    public static string Success(string entityName, string action) =>
        Localize("Common.Success", "{0} {1} successfully.", entityName, action);
#pragma warning restore S1133
}

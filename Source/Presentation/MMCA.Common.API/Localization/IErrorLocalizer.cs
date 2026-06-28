namespace MMCA.Common.API.Localization;

/// <summary>
/// Localizes a domain <c>Error</c>'s human-readable message at the HTTP edge, keyed by its stable
/// machine <c>Code</c> (ADR-027). Domain/handler/<c>Result</c> code stays culture-agnostic; only the
/// edge speaks a culture. When no resource key matches the code, the caller's existing English message
/// is returned unchanged, so an untranslated code degrades gracefully rather than throwing.
/// </summary>
public interface IErrorLocalizer
{
    /// <summary>
    /// Returns the localized message for <paramref name="code"/> against the current UI culture, or
    /// <paramref name="fallbackMessage"/> when the code is empty or no registered resource source has a key.
    /// </summary>
    /// <param name="code">The stable machine error code (e.g. <c>"PhoneNumber.Empty"</c>).</param>
    /// <param name="fallbackMessage">The original English message to use when no translation exists.</param>
    string Localize(string code, string fallbackMessage);
}

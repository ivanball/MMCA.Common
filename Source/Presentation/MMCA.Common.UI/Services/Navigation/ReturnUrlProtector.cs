namespace MMCA.Common.UI.Services.Navigation;

/// <summary>
/// Validates and sanitizes <c>returnUrl</c> query parameters to prevent open-redirect
/// vulnerabilities. Only same-origin relative paths beginning with a single forward slash
/// are accepted; absolute URLs, protocol-relative URLs, control characters, and backslash
/// sequences are all rejected and replaced with the configured fallback.
/// </summary>
public static class ReturnUrlProtector
{
    /// <summary>
    /// Returns <paramref name="candidate"/> if it is a safe same-origin relative path,
    /// otherwise returns <paramref name="fallback"/>.
    /// </summary>
    /// <param name="candidate">The user-supplied return URL to validate. May be <see langword="null"/>.</param>
    /// <param name="fallback">The path to return when <paramref name="candidate"/> is missing or unsafe. Defaults to <c>"/"</c>.</param>
    /// <returns>A safe relative path beginning with <c>'/'</c>.</returns>
    public static string Sanitize(string? candidate, string fallback = "/")
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return fallback;
        }

        // Must be an absolute path (starts with a single '/'), not a protocol-relative URL
        // and not a scheme-prefixed absolute URL like "http://" or "javascript:".
        if (candidate[0] != '/')
        {
            return fallback;
        }

        // "//" or "/\" is interpreted by browsers as the start of an authority component,
        // which would redirect off-host. Reject both forms.
        if (candidate.Length > 1 && (candidate[1] == '/' || candidate[1] == '\\'))
        {
            return fallback;
        }

        // Backslashes anywhere are normalized by some browsers to forward slashes and create
        // open-redirect vectors (e.g. "/\\evil.com" becomes "//evil.com" in Chrome).
        if (candidate.Contains('\\', StringComparison.Ordinal))
        {
            return fallback;
        }

        // Reject control characters (CR, LF, NUL, tab, etc.) — header injection / response
        // splitting / cookie smuggling vectors.
        if (candidate.Any(char.IsControl))
        {
            return fallback;
        }

        // Final defence: ensure the candidate parses as a well-formed relative URI reference.
        if (!Uri.TryCreate(candidate, UriKind.Relative, out _))
        {
            return fallback;
        }

        return candidate;
    }
}

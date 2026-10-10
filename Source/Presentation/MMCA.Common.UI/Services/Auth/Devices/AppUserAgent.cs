using System.Globalization;
using System.Text;

namespace MMCA.Common.UI.Services.Auth.Devices;

/// <summary>
/// Builds the <c>User-Agent</c> a native MMCA app sends, e.g. <c>AtlDevCon/1.9.2 (Android 15; MmcaApp)</c>,
/// so the signed-in devices list can name the app instead of reporting an unrecognized device.
/// </summary>
public static class AppUserAgent
{
    /// <summary>The marker token that identifies a native MMCA app in a user agent.</summary>
    public const string Marker = "MmcaApp";

    /// <summary>The product name used when nothing of the app name survives sanitizing.</summary>
    private const string FallbackName = "App";

    /// <summary>The punctuation an RFC 9110 token may hold besides letters and digits.</summary>
    private const string TokenSymbols = "!#$%&'*+-.^_`|~";

    /// <summary>
    /// Builds a header-safe user agent for a native app.
    /// </summary>
    /// <param name="appName">The app's display name.</param>
    /// <param name="appVersion">The app's version string.</param>
    /// <param name="platform">The device platform name.</param>
    /// <param name="osVersion">The OS version, when known.</param>
    /// <returns>The user-agent header value.</returns>
    public static string Build(string appName, string appVersion, string platform, string? osVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);

        var name = ToToken(appName);
        if (name.Length == 0)
        {
            name = FallbackName;
        }

        var version = ToToken(appVersion ?? string.Empty);
        var product = version.Length == 0 ? name : $"{name}/{version}";

        var platformName = ToCommentText(NormalizePlatform(platform ?? string.Empty));
        var os = ToCommentText(osVersion ?? string.Empty);
        var system = string.Join(' ', new[] { platformName, os }.Where(part => part.Length > 0));

        return system.Length == 0
            ? $"{product} ({Marker})"
            : $"{product} ({system}; {Marker})";
    }

    /// <summary>
    /// MAUI names the desktop heads after their UI stacks (<c>WinUI</c>, <c>MacCatalyst</c>); the user
    /// agent carries the operating system name the device label's platform table knows instead.
    /// </summary>
    private static string NormalizePlatform(string platform)
    {
        var trimmed = platform.Trim();

        if (trimmed.Equals("WinUI", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows";
        }

        return trimmed.Equals("MacCatalyst", StringComparison.OrdinalIgnoreCase) ? "macOS" : trimmed;
    }

    /// <summary>
    /// Reduces text to an RFC 9110 token: accents fold to their base letter, and every run of
    /// characters a token cannot hold (spaces, parentheses, slashes, non-ASCII) becomes one hyphen.
    /// </summary>
    private static string ToToken(string value) => Sanitize(value, IsTokenChar, '-');

    /// <summary>
    /// Reduces text to what a header comment holds without ending early: visible ASCII other than the
    /// comment delimiters, the escape and the separator, with every other run collapsed to one space.
    /// </summary>
    private static string ToCommentText(string value) => Sanitize(value, IsCommentChar, ' ');

    private static string Sanitize(string value, Func<char, bool> isAllowed, char replacement)
    {
        var folded = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(folded.Length);
        var pendingReplacement = false;

        foreach (var c in folded)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (!isAllowed(c))
            {
                pendingReplacement = true;
                continue;
            }

            if (pendingReplacement && builder.Length > 0)
            {
                builder.Append(replacement);
            }

            builder.Append(c);
            pendingReplacement = false;
        }

        return builder.ToString();
    }

    private static bool IsTokenChar(char c) => char.IsAsciiLetterOrDigit(c) || TokenSymbols.Contains(c, StringComparison.Ordinal);

    private static bool IsCommentChar(char c) => c is > ' ' and < '\u007F' and not '(' and not ')' and not '\\' and not ';';
}

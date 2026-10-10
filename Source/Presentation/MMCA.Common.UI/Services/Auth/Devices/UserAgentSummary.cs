using Microsoft.Extensions.Localization;

namespace MMCA.Common.UI.Services.Auth.Devices;

/// <summary>
/// Turns a raw <c>User-Agent</c> header into the two words a person recognizes their own device by:
/// the browser and the platform. Deliberately small and deliberately not a UA database.
/// <para>
/// A device list only has to let someone answer "is that me?". A full UA parser (and the library
/// plus data file it needs) buys precision nobody reads, while the browser-and-platform pair is
/// enough to tell a phone from a work laptop. Anything unrecognized reports <see langword="null"/>,
/// and the page shows its own "unknown device" wording rather than the raw header, which is neither
/// readable nor localizable.
/// </para>
/// <para>
/// The two parts are returned separately, never joined: composing "Chrome on Windows" in code would
/// hard-code English word order (ADR-027). The caller formats them through a resource string.
/// </para>
/// </summary>
internal static class UserAgentSummary
{
    /// <summary>
    /// Order matters: every Chromium browser also says "Chrome", and Chrome and Edge both say
    /// "Safari", so the most specific token has to win. Each entry is (token in the header, name to
    /// show).
    /// </summary>
    private static readonly (string Token, string Name)[] Browsers =
    [
        ("Edg/", "Edge"),
        ("EdgiOS/", "Edge"),
        ("EdgA/", "Edge"),
        ("OPR/", "Opera"),
        ("Opera", "Opera"),
        ("SamsungBrowser", "Samsung Internet"),
        ("CriOS/", "Chrome"),
        ("FxiOS/", "Firefox"),
        ("Firefox/", "Firefox"),
        ("Chrome/", "Chrome"),
        ("Safari/", "Safari"),
    ];

    /// <summary>
    /// Platform tokens, most specific first: an iPad reports "Macintosh" in desktop mode and
    /// Android reports "Linux".
    /// </summary>
    private static readonly (string Token, string Name)[] Platforms =
    [
        ("Windows Phone", "Windows Phone"),
        ("Windows", "Windows"),
        ("Android", "Android"),
        ("iPhone", "iOS"),
        ("iPad", "iPadOS"),
        ("iPod", "iOS"),
        ("CrOS", "ChromeOS"),
        ("Mac OS X", "macOS"),
        ("Macintosh", "macOS"),
        ("Linux", "Linux"),
    ];

    /// <summary>
    /// The one-word platform names a native app header carries (see <see cref="AppUserAgent"/>),
    /// including MAUI's names for the desktop heads, mapped to the names <see cref="Platforms"/> shows.
    /// </summary>
    private static readonly (string Word, string Name)[] AppPlatforms =
    [
        ("Android", "Android"),
        ("iOS", "iOS"),
        ("iPadOS", "iPadOS"),
        ("Windows", "Windows"),
        ("WinUI", "Windows"),
        ("macOS", "macOS"),
        ("MacCatalyst", "macOS"),
        ("tvOS", "tvOS"),
        ("watchOS", "watchOS"),
    ];

    /// <summary>
    /// Reads the browser and platform out of a user-agent header.
    /// </summary>
    /// <param name="userAgent">The raw header, which may be missing, empty, or unrecognizable.</param>
    /// <returns>
    /// The browser name and the platform name, either of which may be <see langword="null"/> when
    /// the header does not identify it.
    /// </returns>
    public static (string? Browser, string? Platform) Parse(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return (null, null);
        }

        return (Match(userAgent, Browsers), Match(userAgent, Platforms));
    }

    /// <summary>
    /// The device label the signed-in devices views show for a session's user agent (the logic shared
    /// by the Sessions page and the administrator's sessions table).
    /// </summary>
    /// <param name="userAgent">The raw header, which may be missing, empty, or unrecognizable.</param>
    /// <param name="localizer">The localizer the label's resource formats are read through.</param>
    /// <returns>The localized device label.</returns>
    public static string Describe(string? userAgent, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        // The native app marker is read BEFORE the browser tokens: an app header that also carries a
        // WebView's "Chrome/" token is still the app.
        var (appName, appPlatform) = ParseApp(userAgent);
        if (appName is not null)
        {
            return appPlatform is null
                ? appName
                : localizer["Auth.Sessions.Device.AppFormat", appName, appPlatform].Value;
        }

        var (browser, platform) = Parse(userAgent);

        return (browser, platform) switch
        {
            (not null, not null) => localizer["Auth.Sessions.Device.Format", browser, platform].Value,
            (not null, null) => browser,
            (null, not null) => platform,
            _ => localizer["Auth.Sessions.Device.Unknown"].Value,
        };
    }

    /// <summary>
    /// Reads a native MMCA app header (<see cref="AppUserAgent"/>): the app name is the product token's
    /// name and the platform is the first word of the comment that carries the marker.
    /// </summary>
    private static (string? AppName, string? Platform) ParseApp(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return (null, null);
        }

        var commentStart = userAgent.IndexOf('(', StringComparison.Ordinal);
        if (commentStart <= 0)
        {
            return (null, null);
        }

        var commentEnd = userAgent.IndexOf(')', commentStart);
        var comment = commentEnd < 0 ? userAgent[(commentStart + 1)..] : userAgent[(commentStart + 1)..commentEnd];
        var parts = comment.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!parts.Contains(AppUserAgent.Marker, StringComparer.Ordinal))
        {
            return (null, null);
        }

        var product = userAgent[..commentStart].Trim();
        var slash = product.IndexOf('/', StringComparison.Ordinal);
        var appName = slash < 0 ? product : product[..slash];
        if (appName.Length == 0)
        {
            return (null, null);
        }

        var platformWord = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return platformWord.Equals(AppUserAgent.Marker, StringComparison.Ordinal)
            ? (appName, null)
            : (appName, MatchAppPlatform(platformWord));
    }

    /// <summary>
    /// The app header names its platform with one word (the OS name, or MAUI's name for the desktop
    /// heads); the label uses the platform table's name for it.
    /// </summary>
    private static string MatchAppPlatform(string platformWord)
    {
        foreach (var (word, name) in AppPlatforms)
        {
            if (platformWord.Equals(word, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return Match(platformWord, Platforms) ?? platformWord;
    }

    private static string? Match(string userAgent, (string Token, string Name)[] candidates)
    {
        foreach (var (token, name) in candidates)
        {
            if (userAgent.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return null;
    }
}

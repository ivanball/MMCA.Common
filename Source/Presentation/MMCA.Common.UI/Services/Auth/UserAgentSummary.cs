namespace MMCA.Common.UI.Services.Auth;

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

namespace MMCA.Common.UI.Services.Auth.Devices;

/// <summary>
/// Builds the <c>User-Agent</c> a native MMCA app sends, e.g. <c>AtlDevCon/1.9.2 (Android 15; MmcaApp)</c>,
/// so the signed-in devices list can name the app instead of reporting an unrecognized device.
/// </summary>
public static class AppUserAgent
{
    /// <summary>The marker token that identifies a native MMCA app in a user agent.</summary>
    public const string Marker = "MmcaApp";

    /// <summary>
    /// Builds a header-safe user agent for a native app.
    /// </summary>
    /// <param name="appName">The app's display name.</param>
    /// <param name="appVersion">The app's version string.</param>
    /// <param name="platform">The device platform name.</param>
    /// <param name="osVersion">The OS version, when known.</param>
    /// <returns>The user-agent header value.</returns>
    public static string Build(string appName, string appVersion, string platform, string? osVersion) =>
        throw new NotImplementedException("TEST-FIRST STUB: the implementation lands in a separate change.");
}

using MMCA.Common.UI.Services.Auth.Devices;

namespace MMCA.Common.UI.Maui.Services;

/// <summary>
/// Stamps the native app's <c>User-Agent</c> (<see cref="AppUserAgent"/>, e.g.
/// <c>AtlDevCon/1.9.2 (Android 15; MmcaApp)</c>) on every <c>"APIClient"</c> request, so the
/// refresh session the server records at login, register and refresh names the app and the signed-in
/// devices list reads "{App} app on {Platform}" instead of "Unrecognized device". The browser heads
/// get the visitor's own header through <c>BrowserOriginHandler</c> (MMCA.Common.UI.Web); a MAUI head
/// otherwise sends none. Registered by <see cref="DependencyInjection"/>'s
/// <c>AddCommonMauiAppUserAgent()</c>.
/// </summary>
internal sealed partial class MauiAppUserAgentHandler : DelegatingHandler
{
    /// <summary>
    /// Built once per process: the app name and version, the platform and the OS version cannot
    /// change while the app runs, and <see cref="AppUserAgent.Build"/> already sanitizes them into a
    /// legal header value.
    /// </summary>
    private static readonly Lazy<string> UserAgent = new(() => AppUserAgent.Build(
        AppInfo.Name,
        AppInfo.VersionString,
        DeviceInfo.Platform.ToString(),
        DeviceInfo.VersionString));

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Replace rather than append: the platform handler's own default (Dalvik/..., CFNetwork/...)
        // would otherwise sit beside it, and the server records the header as one string.
        request.Headers.UserAgent.Clear();
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent.Value);

        return base.SendAsync(request, cancellationToken);
    }
}

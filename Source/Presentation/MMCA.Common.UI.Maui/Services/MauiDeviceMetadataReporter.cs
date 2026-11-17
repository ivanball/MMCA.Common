using Microsoft.Extensions.Logging;
using MMCA.Common.Shared.Auth;
using MMCA.Common.UI.Services.Auth.Devices;
using MMCA.Common.UI.Services.Capabilities.DeviceStorage;

namespace MMCA.Common.UI.Maui.Services;

/// <summary>
/// The native <see cref="IDeviceMetadataReporter"/>: reads the device from MAUI's <see cref="DeviceInfo"/>
/// (name, model, manufacturer, platform, idiom as the form factor, physical or virtual) plus a per-install
/// id kept in <see cref="IDevicePreferences"/>, and PUTs it to <c>auth/sessions/current/device</c> over the
/// authenticated <c>"APIClient"</c>, whose bearer names the session the server records it on.
/// <para>
/// The id is a random GUID created on first use and stored under its own key: iOS exposes no stable
/// device identifier, and a GUID per install is enough to tell two phones of the same model apart. It is
/// deliberately not the push installation id, which the push service replaces when the server reports a
/// collision. Never throws: a failed report is logged, answers false (so the refresh backfill retries),
/// and the session keeps its user-agent description.
/// Registered by <see cref="DependencyInjection"/>'s <c>AddMauiDeviceCapabilities()</c>.
/// </para>
/// </summary>
internal sealed partial class MauiDeviceMetadataReporter(
    IHttpClientFactory httpClientFactory,
    IDevicePreferences devicePreferences,
    ILogger<MauiDeviceMetadataReporter> logger) : IDeviceMetadataReporter
{
    private const string InstallationIdKey = "mmca.device.installationId";

    /// <inheritdoc />
    public async Task<bool> ReportAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var device = new DeviceMetadata(
                await GetOrCreateInstallationIdAsync(cancellationToken).ConfigureAwait(false),
                DeviceInfo.Current.Name,
                DeviceInfo.Current.Model,
                DeviceInfo.Current.Manufacturer,
                DeviceInfo.Current.Platform.ToString(),
                DeviceInfo.Current.Idiom.ToString(),
                DeviceInfo.Current.DeviceType.ToString());

            using var client = httpClientFactory.CreateClient("APIClient");
            using var response = await client.PutAsJsonAsync(
                new Uri("auth/sessions/current/device", UriKind.Relative),
                device,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // A 404 is a token with no sid (minted before sid shipped) or a session that just ended:
                // the next refresh mints a token that names a live session, and the backfill retries.
                LogReportRejected(logger, (int)response.StatusCode);
                return false;
            }

            return true;
        }
#pragma warning disable CA1031 // Do not catch general exception types: the report is best-effort
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogReportFailed(logger, ex);
            return false;
        }
    }

    private async Task<string> GetOrCreateInstallationIdAsync(CancellationToken cancellationToken)
    {
        var existing = await devicePreferences.GetAsync<string?>(InstallationIdKey, null, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        var created = Guid.NewGuid().ToString("N");
        await devicePreferences.SetAsync(InstallationIdKey, created, cancellationToken).ConfigureAwait(false);
        return created;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Device metadata report rejected with status {StatusCode}")]
    private static partial void LogReportRejected(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Device metadata report failed")]
    private static partial void LogReportFailed(ILogger logger, Exception exception);
}

using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using MMCA.Common.UI.Services.Capabilities;

namespace MMCA.Common.UI.Maui.Capabilities;

/// <summary>
/// Native push registration orchestration (ADR-044). Asks the app's
/// <see cref="IPushDeviceTokenProvider"/> for the platform token, keeps a stable
/// client-generated installation id in device preferences, and syncs the installation to the
/// server's <c>Notifications/Devices</c> endpoints over the authenticated API client. Inert by
/// construction until the app registers a credentialed token provider (the default provider
/// yields no token). Never throws: registration is a best-effort side channel.
/// </summary>
public sealed partial class MauiPushRegistrationService(
    IPushDeviceTokenProvider tokenProvider,
    IHttpClientFactory httpClientFactory,
    IDevicePreferences devicePreferences,
    ILogger<MauiPushRegistrationService> logger) : IPushRegistrationService
{
    private const string InstallationIdKey = "mmca.push.installationId";

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public async Task<bool> RegisterAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var token = await tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
            if (token is null)
            {
                return false;
            }

            var installationId = await GetOrCreateInstallationIdAsync(cancellationToken).ConfigureAwait(false);
            using var client = httpClientFactory.CreateClient("APIClient");
            var response = await client.PutAsJsonAsync(
                new Uri("Notifications/Devices", UriKind.Relative),
                new { InstallationId = installationId, token.Platform, PushChannel = token.Token },
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                LogRegistrationRejected(logger, (int)response.StatusCode);
                return false;
            }

            return true;
        }
#pragma warning disable CA1031 // Do not catch general exception types — registration is best-effort
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRegistrationFailed(logger, ex);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task UnregisterAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var installationId = await devicePreferences.GetAsync<string?>(InstallationIdKey, null, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(installationId))
            {
                return;
            }

            using var client = httpClientFactory.CreateClient("APIClient");
            using var response = await client.DeleteAsync(
                new Uri($"Notifications/Devices/{Uri.EscapeDataString(installationId)}", UriKind.Relative),
                cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Do not catch general exception types — unregistration is best-effort
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogUnregistrationFailed(logger, ex);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Push device registration rejected with status {StatusCode}")]
    private static partial void LogRegistrationRejected(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Push device registration failed")]
    private static partial void LogRegistrationFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Push device unregistration failed")]
    private static partial void LogUnregistrationFailed(ILogger logger, Exception exception);
}

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using MMCA.Common.UI.Services.Capabilities.DeviceStorage;
using MMCA.Common.UI.Services.Capabilities.Notifications;

namespace MMCA.Common.UI.Maui.Capabilities.Notifications;

/// <summary>
/// Native push registration orchestration (ADR-044). Asks the app's
/// <see cref="IPushDeviceTokenProvider"/> for the platform token, keeps a stable
/// client-generated installation id in device preferences (replaced, and the PUT retried once, only
/// when the server answers 409 because the id belongs to another device), and syncs the
/// installation to the server's <c>Notifications/Devices</c> endpoints over the authenticated API
/// client. Inert by
/// construction until the app registers a credentialed token provider (the default provider
/// yields no token). Never throws: registration is a best-effort side channel.
/// </summary>
public sealed partial class MauiPushRegistrationService(
    IPushDeviceTokenProvider tokenProvider,
    IHttpClientFactory httpClientFactory,
    IDevicePreferences devicePreferences,
    ILogger<MauiPushRegistrationService> logger) : IPushRegistrationService, IDisposable
{
    private const string InstallationIdKey = "mmca.push.installationId";

    private readonly SemaphoreSlim _registration = new(1, 1);

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    /// <remarks>
    /// Serialized end to end. <c>OnNewToken</c> fires a registration on a Firebase thread while the
    /// login pass runs one from the renderer; with an empty preference store (an iOS reinstall keeps
    /// the Keychain session but not the preferences) both passes used to create an installation id
    /// and PUT it, leaving two server rows for one device. A lock around the id creation alone would
    /// still let both passes PUT, one with a stale id.
    /// </remarks>
    public async Task<bool> RegisterAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _registration.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await RegisterCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _registration.Release();
            }
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
    public void Dispose() => _registration.Dispose();

    private async Task<bool> RegisterCoreAsync(CancellationToken cancellationToken)
    {
        var token = await tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return false;
        }

        var installationId = await GetOrCreateInstallationIdAsync(cancellationToken).ConfigureAwait(false);
        using var client = httpClientFactory.CreateClient("APIClient");
        var status = await PutInstallationAsync(client, installationId, token, cancellationToken).ConfigureAwait(false);

        if (status == HttpStatusCode.Conflict)
        {
            // The server holds this id for another user's device (a restored backup or a copied
            // preference store can carry an id to a second phone). Ids are random GUIDs, so a fresh
            // one is all it takes; persist it first so later passes and the logout delete use it.
            installationId = await CreateInstallationIdAsync(cancellationToken).ConfigureAwait(false);
            status = await PutInstallationAsync(client, installationId, token, cancellationToken).ConfigureAwait(false);
        }

        if ((int)status is < 200 or > 299)
        {
            LogRegistrationRejected(logger, (int)status);
            return false;
        }

        return true;
    }

    private static async Task<HttpStatusCode> PutInstallationAsync(
        HttpClient client,
        string installationId,
        PushDeviceToken token,
        CancellationToken cancellationToken)
    {
        using var response = await client.PutAsJsonAsync(
            new Uri("Notifications/Devices", UriKind.Relative),
            new { InstallationId = installationId, token.Platform, PushChannel = token.Token },
            cancellationToken).ConfigureAwait(false);
        return response.StatusCode;
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

        return await CreateInstallationIdAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> CreateInstallationIdAsync(CancellationToken cancellationToken)
    {
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

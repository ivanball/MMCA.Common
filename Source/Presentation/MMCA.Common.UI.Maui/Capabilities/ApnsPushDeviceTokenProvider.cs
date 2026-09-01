#if IOS || MACCATALYST
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MMCA.Common.UI.Services.Capabilities;
using UIKit;
using UserNotifications;

namespace MMCA.Common.UI.Maui.Capabilities;

/// <summary>
/// Credentialed iOS <see cref="IPushDeviceTokenProvider"/> (ADR-044): registers with APNs and
/// yields the hex device token Azure Notification Hubs targets as the <c>apns</c> platform.
/// Gated on <c>Push:Apns:Enabled</c> so users are never shown the notification-permission prompt
/// while the head ships without the aps-environment entitlement; with the flag off (the default)
/// the provider yields <see langword="null"/> and the pipeline stays inert. Never throws: failures
/// and the no-entitlement timeout log and yield <see langword="null"/>.
/// <para>
/// The head still owns the platform wiring: the aps-environment entitlement, the Info.plist
/// background modes, and the two <c>AppDelegate</c> callbacks that publish into
/// <see cref="ApnsTokenBridge"/>.
/// </para>
/// </summary>
/// <param name="configuration">Supplies the <c>Push:Apns:Enabled</c> gate.</param>
/// <param name="logger">Records best-effort registration failures.</param>
public sealed partial class ApnsPushDeviceTokenProvider(
    IConfiguration configuration,
    ILogger<ApnsPushDeviceTokenProvider> logger) : IPushDeviceTokenProvider
{
    /// <summary>Configuration key gating APNs registration; false (the default) keeps it inert.</summary>
    public const string EnabledConfigKey = "Push:Apns:Enabled";

    private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public async Task<PushDeviceToken?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue<bool>(EnabledConfigKey))
        {
            return null;
        }

        try
        {
            var existing = ApnsTokenBridge.CurrentToken;
            if (existing is not null)
            {
                return new PushDeviceToken(Platform: "apns", Token: existing);
            }

            // Interface contract: request permission as needed. A denial does not block
            // registration (silent pushes still deliver); alerts simply will not display.
            _ = await UNUserNotificationCenter.Current.RequestAuthorizationAsync(
                    UNAuthorizationOptions.Alert | UNAuthorizationOptions.Badge | UNAuthorizationOptions.Sound)
                .ConfigureAwait(false);

            // Taken BEFORE RegisterForRemoteNotifications: the bridge re-arms its rendezvous on each
            // published callback, so this is the handle for the attempt started on the next line
            // rather than a previous attempt's already-decided outcome.
            var wait = ApnsTokenBridge.WaitForTokenAsync();
            await MainThread.InvokeOnMainThreadAsync(static () =>
                UIApplication.SharedApplication.RegisterForRemoteNotifications()).ConfigureAwait(false);

            var completed = await Task.WhenAny(wait, Task.Delay(RegistrationTimeout, cancellationToken)).ConfigureAwait(false);
            var token = completed == wait ? await wait.ConfigureAwait(false) : null;
            return token is null ? null : new PushDeviceToken(Platform: "apns", Token: token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
#pragma warning disable CA1031 // Token minting is best-effort; a throw here would break the registration caller.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogTokenFailed(logger, ex);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "APNs registration failed; push registration stays inert.")]
    private static partial void LogTokenFailed(ILogger logger, Exception ex);
}
#endif

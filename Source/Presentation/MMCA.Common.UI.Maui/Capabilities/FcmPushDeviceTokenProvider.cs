#if ANDROID
using Android.Gms.Extensions;
using Firebase;
using Firebase.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MMCA.Common.UI.Services.Capabilities;

namespace MMCA.Common.UI.Maui.Capabilities;

/// <summary>
/// Credentialed Android <see cref="IPushDeviceTokenProvider"/> (ADR-044): mints the FCM
/// registration token that Azure Notification Hubs targets as the <c>fcmv1</c> platform.
/// Firebase is initialized manually from the <c>Push:Fcm</c> configuration section (no
/// google-services.json build dependency), so while that section is blank the provider yields
/// <see langword="null"/> and the whole registration pipeline stays inert, exactly like the
/// framework's null default. Never throws: any Firebase/Play-services failure logs and yields
/// <see langword="null"/> (registration is a best-effort side channel).
/// <para>
/// The head still owns its own credentials (the four <c>Push:Fcm</c> values) and the
/// POST_NOTIFICATIONS manifest declaration.
/// </para>
/// </summary>
/// <param name="configuration">Supplies the <c>Push:Fcm</c> credentials section.</param>
/// <param name="localNotifications">Used to request the Android 13+ notification permission.</param>
/// <param name="logger">Records best-effort token-minting failures.</param>
public sealed partial class FcmPushDeviceTokenProvider(
    IConfiguration configuration,
    ILocalNotificationService localNotifications,
    ILogger<FcmPushDeviceTokenProvider> logger) : IPushDeviceTokenProvider
{
    /// <summary>Configuration section holding the Firebase credentials; blank keeps the provider inert.</summary>
    public const string ConfigSection = "Push:Fcm";

    /// <inheritdoc />
    public async Task<PushDeviceToken?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        var section = configuration.GetSection(ConfigSection);
        var projectId = section["ProjectId"];
        var applicationId = section["ApplicationId"];
        var apiKey = section["ApiKey"];
        var senderId = section["ProjectNumber"];
        if (string.IsNullOrWhiteSpace(projectId)
            || string.IsNullOrWhiteSpace(applicationId)
            || string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(senderId))
        {
            return null;
        }

        try
        {
            EnsureFirebaseApp(projectId, applicationId, apiKey, senderId);

            // Interface contract: implementations request notification permission as needed.
            // Android 13+ POST_NOTIFICATIONS through the same path local notifications use; a
            // denial does not block registration (the token still carries data messages).
            _ = await localNotifications.RequestPermissionAsync(cancellationToken).ConfigureAwait(false);

            var token = (await FirebaseMessaging.Instance.GetToken()
                .AsAsync<Java.Lang.String>().ConfigureAwait(false))?.ToString();
            return string.IsNullOrWhiteSpace(token)
                ? null
                : new PushDeviceToken(Platform: "fcmv1", Token: token);
        }
#pragma warning disable CA1031 // Token minting is best-effort; a throw here would break the registration caller.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogTokenFailed(logger, ex);
            return null;
        }
    }

    private static void EnsureFirebaseApp(string projectId, string applicationId, string apiKey, string senderId)
    {
        var context = global::Android.App.Application.Context;
        if (FirebaseApp.GetApps(context).Count > 0)
        {
            return;
        }

        using var builder = new FirebaseOptions.Builder();
        var options = builder
            .SetProjectId(projectId)
            .SetApplicationId(applicationId)
            .SetApiKey(apiKey)
            .SetGcmSenderId(senderId)
            .Build();
        FirebaseApp.InitializeApp(context, options);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "FCM token retrieval failed; push registration stays inert.")]
    private static partial void LogTokenFailed(ILogger logger, Exception ex);
}
#endif

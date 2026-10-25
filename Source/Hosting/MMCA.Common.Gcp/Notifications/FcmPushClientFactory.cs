using Google.Api.Gax;
using Google.Cloud.Firestore;

namespace MMCA.Common.Gcp.Notifications;

/// <summary>
/// Builds the Firestore database and the FCM client <c>AddFirebaseCloudMessagingPush</c> registers,
/// from <see cref="FcmNativePushSettings"/>. Both target the same project, since the registry and the
/// tokens it records belong to one Firebase app.
/// </summary>
internal static class FcmPushClientFactory
{
    /// <summary>
    /// Maps the settings onto a <see cref="FirestoreDbBuilder"/>: the configured project's default
    /// database over Application Default Credentials. The <c>FIRESTORE_EMULATOR_HOST</c> variable is
    /// ignored (<see cref="EmulatorDetection.None"/>, the builder default), so the endpoint is decided
    /// by configuration alone, the same posture as the Cloud Storage adapter; an emulator run
    /// registers its own <see cref="FirestoreDb"/> first.
    /// </summary>
    /// <param name="settings">The bound settings.</param>
    /// <returns>The builder.</returns>
    internal static FirestoreDbBuilder BuildFirestoreBuilder(FcmNativePushSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new FirestoreDbBuilder
        {
            ProjectId = settings.ProjectId,
            EmulatorDetection = EmulatorDetection.None,
        };
    }

    /// <summary>Creates the Firestore database from <see cref="BuildFirestoreBuilder"/>.</summary>
    /// <param name="settings">The bound settings.</param>
    /// <returns>The Firestore database.</returns>
    internal static FirestoreDb CreateFirestore(FcmNativePushSettings settings) => BuildFirestoreBuilder(settings).Build();

    /// <summary>Creates the FCM client, which creates its own named Firebase app on first use.</summary>
    /// <param name="settings">The bound settings.</param>
    /// <returns>The FCM client.</returns>
    internal static FirebaseMessagingClient CreateMessaging(FcmNativePushSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new FirebaseMessagingClient(settings.ProjectId!);
    }
}

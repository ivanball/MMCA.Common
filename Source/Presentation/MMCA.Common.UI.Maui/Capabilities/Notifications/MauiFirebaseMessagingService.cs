#if ANDROID
using Android.App;
using Firebase.Messaging;
using MMCA.Common.UI.Services.Capabilities.Notifications;

namespace MMCA.Common.UI.Maui.Capabilities.Notifications;

/// <summary>
/// Receives FCM lifecycle callbacks (ADR-044). <see cref="OnNewToken"/> fires when Play services
/// rotates the registration token; re-registering re-upserts this device's installation so the
/// server-side push handle never goes stale. Message display is deliberately not handled here:
/// notification-type pushes are rendered by the system tray while the app is backgrounded, and the
/// in-app SignalR hub owns foreground delivery.
/// <para>
/// The <c>Mmca</c>-free name would collide with the Firebase base class, so the package prefix used
/// by every other type here is kept. The <c>[Service]</c>/<c>[IntentFilter]</c> attributes are
/// merged into the consuming head's manifest by the Android build, so a head only has to reference
/// this package; the credentials themselves stay app-side.
/// </para>
/// </summary>
[Service(Exported = false)]
[IntentFilter(["com.google.firebase.MESSAGING_EVENT"])]
public sealed class MauiFirebaseMessagingService : FirebaseMessagingService
{
    /// <inheritdoc />
    public override void OnNewToken(string token)
    {
        base.OnNewToken(token);

        // Fire-and-forget by design: a rotation while signed out simply fails the
        // authenticated upsert, and the next login registration pass
        // (PushRegistrationListener) heals it.
        _ = IPlatformApplication.Current?.Services
            .GetService<IPushRegistrationService>()?.RegisterAsync();
    }
}
#endif

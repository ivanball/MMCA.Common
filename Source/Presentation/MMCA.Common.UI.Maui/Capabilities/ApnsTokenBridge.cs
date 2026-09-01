#if IOS || MACCATALYST
namespace MMCA.Common.UI.Maui.Capabilities;

/// <summary>
/// Hands the APNs device token from the head's <c>AppDelegate</c> UIKit callbacks to
/// <see cref="ApnsPushDeviceTokenProvider"/> (ADR-044). UIKit reports remote-notification
/// registration only through the app delegate, so a static rendezvous is the boundary between the
/// callback and the awaiting provider.
/// <para>
/// The delegate hooks themselves stay app-side (the ObjC registrar binds the selectors to the
/// head's own delegate instance). A head wires them like this:
/// </para>
/// <code>
/// [Export("application:didRegisterForRemoteNotificationsWithDeviceToken:")]
/// public void RegisteredForRemoteNotifications(UIApplication _, NSData deviceToken)
///     =&gt; ApnsTokenBridge.Publish(Convert.ToHexStringLower([.. deviceToken]));
///
/// [Export("application:didFailToRegisterForRemoteNotificationsWithError:")]
/// public void FailedToRegisterForRemoteNotifications(UIApplication _, NSError __)
///     =&gt; ApnsTokenBridge.Publish(null);
/// </code>
/// </summary>
public static class ApnsTokenBridge
{
    private static TaskCompletionSource<string?> _pending = NewPending();

    /// <summary>The most recent APNs token (lowercase hex), or null when none was issued.</summary>
    public static string? CurrentToken { get; private set; }

    /// <summary>
    /// Completes when the NEXT registration callback reports success or failure.
    /// <para>
    /// The rendezvous is re-armed per attempt on purpose. A single one-shot source completed by a
    /// failed first registration would hand every later caller that same decided outcome instantly,
    /// so a retry would report failure without ever waiting for the registration it just started.
    /// Call this BEFORE asking UIKit to register, so the attempt's own callback is the one awaited.
    /// </para>
    /// </summary>
    public static Task<string?> WaitForTokenAsync() => Volatile.Read(ref _pending).Task;

    /// <summary>Publishes a callback outcome; null means registration failed.</summary>
    /// <param name="hexToken">The lowercase-hex device token, or null on failure.</param>
    public static void Publish(string? hexToken)
    {
        if (hexToken is not null)
        {
            CurrentToken = hexToken;
        }

        // Re-arm BEFORE completing: a waiter arriving between the two lines gets the next attempt's
        // rendezvous rather than this one's already-decided outcome.
        var completed = Interlocked.Exchange(ref _pending, NewPending());
        completed.TrySetResult(hexToken);
    }

    private static TaskCompletionSource<string?> NewPending() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
#endif

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
    private static readonly TaskCompletionSource<string?> Pending =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The most recent APNs token (lowercase hex), or null when none was issued.</summary>
    public static string? CurrentToken { get; private set; }

    /// <summary>Completes when the first registration attempt reports success or failure.</summary>
    public static Task<string?> WaitForTokenAsync() => Pending.Task;

    /// <summary>Publishes a callback outcome; null means registration failed.</summary>
    /// <param name="hexToken">The lowercase-hex device token, or null on failure.</param>
    public static void Publish(string? hexToken)
    {
        if (hexToken is not null)
        {
            CurrentToken = hexToken;
        }

        Pending.TrySetResult(hexToken);
    }
}
#endif

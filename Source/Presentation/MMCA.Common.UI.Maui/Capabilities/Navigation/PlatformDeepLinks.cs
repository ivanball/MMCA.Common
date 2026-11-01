#if ANDROID || IOS || MACCATALYST
using MMCA.Common.UI.Services.Capabilities.Navigation;

namespace MMCA.Common.UI.Maui.Capabilities.Navigation;

/// <summary>
/// Turns a link a native head received into an in-app route and publishes it to the
/// <see cref="IDeepLinkDispatcher"/> (ADR-042, ADR-043), so every head reads a link the same way:
/// the scheme and host are dropped, the ENCODED path, query and fragment become the route
/// (<see cref="DeepLinkDispatcher.TryComposeRoute"/>), and a route that is not app-relative is
/// dropped silently instead of thrown out of a platform callback (SEC-Common-88, SEC-ADC-65).
/// <para>
/// The platform callbacks stay in the head (the activity's <c>OnCreate</c> and <c>OnNewIntent</c>,
/// the app delegate's <c>OpenUrl</c> and <c>ContinueUserActivity</c>); each one hands its link here.
/// </para>
/// </summary>
public static class PlatformDeepLinks
{
#if ANDROID
    /// <summary>
    /// Publishes the link an <c>ACTION_VIEW</c> intent carries. Any other intent is ignored.
    /// </summary>
    /// <param name="intent">The launch intent, or the one delivered to <c>OnNewIntent</c>.</param>
    /// <returns>Whether a route was published.</returns>
    /// <remarks>
    /// The activity is exported, so any app on the device can reach it with an explicit intent that
    /// bypasses the manifest filters' scheme and host; the route shape check is what keeps such a
    /// link inside the app origin.
    /// </remarks>
    public static bool TryPublish(Android.Content.Intent? intent)
    {
        if (intent?.Action != Android.Content.Intent.ActionView || intent.Data is not { } data)
        {
            return false;
        }

        return DeepLinkDispatcher.TryComposeRoute(data.EncodedPath, data.EncodedQuery, data.EncodedFragment, out var route)
            && Publish(route);
    }
#endif

#if IOS || MACCATALYST
    /// <summary>
    /// Publishes a custom-scheme URL (<c>OpenUrl</c>) or a Universal Link
    /// (<c>ContinueUserActivity</c>'s <c>WebPageUrl</c>).
    /// </summary>
    /// <param name="url">The link the app delegate received.</param>
    /// <returns>
    /// Whether a route was published; on <see langword="false"/> the delegate falls through to its
    /// base handler.
    /// </returns>
    public static bool TryPublish(Foundation.NSUrl? url)
    {
        if (url is null)
        {
            return false;
        }

        using var components = new Foundation.NSUrlComponents(url, true);
        return DeepLinkDispatcher.TryComposeRoute(
                components.PercentEncodedPath,
                components.PercentEncodedQuery,
                components.PercentEncodedFragment,
                out var route)
            && Publish(route);
    }
#endif

    private static bool Publish(string route)
    {
        if (IPlatformApplication.Current?.Services.GetService(typeof(IDeepLinkDispatcher)) is not IDeepLinkDispatcher dispatcher)
        {
            return false;
        }

        dispatcher.Publish(route);
        return true;
    }
}
#endif

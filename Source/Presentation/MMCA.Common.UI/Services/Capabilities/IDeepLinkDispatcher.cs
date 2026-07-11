namespace MMCA.Common.UI.Services.Capabilities;

/// <summary>
/// The single funnel between native navigation sources (notification taps, home-screen app
/// actions, app links, QR scans) and Blazor routing. Native code publishes an app-relative
/// route; the <c>DeepLinkListener</c> component (rendered in the shared layout) either
/// receives it live via <see cref="RouteRequested"/> or, when the app was cold-started by
/// the tap, drains it from the single-entry pending buffer after first render.
/// </summary>
public interface IDeepLinkDispatcher
{
    /// <summary>Raised when a route is requested while a listener is attached. Runs on the publisher's thread.</summary>
    event EventHandler<DeepLinkRouteEventArgs>? RouteRequested;

    /// <summary>
    /// Publishes a route request. With no listener attached the route is buffered
    /// (last-write-wins, capacity one) for <see cref="TryConsumePending"/>.
    /// </summary>
    void Publish(string route);

    /// <summary>Atomically takes the buffered pending route, if any.</summary>
    bool TryConsumePending(out string? route);
}

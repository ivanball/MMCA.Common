namespace MMCA.Common.UI.Services.Capabilities;

/// <summary>
/// Default <see cref="IDeepLinkDispatcher"/>: raises <see cref="RouteRequested"/> when a
/// listener is attached, otherwise buffers the most recent route (capacity one) so a
/// cold-start tap survives until the Blazor router renders. Registered as a singleton —
/// native callers resolve it from the MAUI root service provider.
/// </summary>
public sealed class DeepLinkDispatcher : IDeepLinkDispatcher
{
    private readonly Lock _gate = new();
    private string? _pendingRoute;

    /// <inheritdoc />
    public event EventHandler<DeepLinkRouteEventArgs>? RouteRequested;

    /// <inheritdoc />
    public void Publish(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);

        var handler = RouteRequested;
        if (handler is null)
        {
            lock (_gate)
            {
                _pendingRoute = route;
            }

            return;
        }

        handler.Invoke(this, new DeepLinkRouteEventArgs(route));
    }

    /// <inheritdoc />
    public bool TryConsumePending(out string? route)
    {
        lock (_gate)
        {
            route = _pendingRoute;
            _pendingRoute = null;
        }

        return route is not null;
    }
}

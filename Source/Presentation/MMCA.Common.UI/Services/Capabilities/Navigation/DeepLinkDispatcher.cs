namespace MMCA.Common.UI.Services.Capabilities.Navigation;

/// <summary>
/// Default <see cref="IDeepLinkDispatcher"/>: raises <see cref="RouteRequested"/> when a
/// listener is attached, otherwise buffers the most recent route (capacity one) so a
/// cold-start tap survives until the Blazor router renders. Registered as a singleton:
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

        // Read the handler INSIDE the lock: the decision (raise now, or buffer for the listener that
        // has not attached yet) and the buffer write have to be one step. Reading it outside allowed
        // this interleaving, which drops the route: Publish sees no handler, the listener subscribes,
        // the listener drains an empty buffer, then Publish writes into a buffer nobody will read
        // again. That is the warm-boot deep link on a native head, where the callback thread and the
        // first render are genuinely concurrent.
        //
        // Under the lock the two orders are the only ones left. If the subscription was visible, the
        // event fires. If it was not, the buffer write completes before the lock is released, and the
        // listener's TryConsumePending, which must take the same lock afterwards, finds the route.
        EventHandler<DeepLinkRouteEventArgs>? handler;
        lock (_gate)
        {
            handler = RouteRequested;
            if (handler is null)
            {
                _pendingRoute = route;
                return;
            }
        }

        // Invoked outside the lock: a listener that navigates on this callback must not run under it.
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

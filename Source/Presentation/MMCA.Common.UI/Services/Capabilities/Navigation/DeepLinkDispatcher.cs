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

    /// <summary>
    /// Whether <paramref name="route"/> is safe to hand to in-app navigation: an app-origin-relative
    /// path, and nothing else.
    /// </summary>
    /// <param name="route">
    /// The candidate route as the platform head assembled it. Nullable and hostile-input tolerant on
    /// purpose: on a native head the value crosses a process boundary from an untrusted caller.
    /// </param>
    /// <returns>
    /// <see langword="true"/> only when the route starts with a single <c>/</c> followed by a
    /// non-slash character, carries no backslash and no control character, and names no scheme.
    /// </returns>
    /// <remarks>
    /// <para>
    /// SEC-Common-88 / SEC-ADC-65. An exported Android activity is reachable by an EXPLICIT intent
    /// from any app on the device, and an explicit intent bypasses the manifest filter's scheme and
    /// host constraints by design, so a hostile app can send <c>https://x//attacker.example/p</c>,
    /// whose path is <c>//attacker.example/p</c>. A route opening with two slashes resolves
    /// PROTOCOL-RELATIVE against the WebView base, so the navigation leaves the app origin and the
    /// default web-view behaviour opens it in the system browser.
    /// </para>
    /// <para>
    /// The check is on the SHAPE of the route, never on the originating host: a host check would
    /// break a home-screen widget whose host is deliberately a placeholder. A scheme allow-list is
    /// deliberately absent for the same reason and replaced by a flat refusal, because a deep link
    /// may never name its own origin or protocol.
    /// </para>
    /// </remarks>
    public static bool IsAppRelativeRoute(string? route)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            return false;
        }

        // A backslash is treated as a path separator by several URL parsers, so "/\attacker.example"
        // is another spelling of the protocol-relative escape. Control characters (a stray CR or LF
        // above all) have no place in a route either.
        if (route.Any(static character => character == '\\' || char.IsControl(character)))
        {
            return false;
        }

        if (StartsWithScheme(route))
        {
            return false;
        }

        // The core rule: exactly one leading slash. "/" alone, "//attacker.example/p" and any
        // relative value are all rejected here.
        return route[0] == '/' && route.Length > 1 && route[1] != '/';
    }

    /// <summary>
    /// Whether the value opens with an RFC 3986 scheme
    /// (<c>ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ) ":"</c>).
    /// </summary>
    private static bool StartsWithScheme(string value)
    {
        var colonIndex = value.IndexOf(':', StringComparison.Ordinal);
        if (colonIndex <= 0 || !char.IsAsciiLetter(value[0]))
        {
            return false;
        }

        for (var i = 1; i < colonIndex; i++)
        {
            var character = value[i];
            if (!char.IsAsciiLetterOrDigit(character) && character != '+' && character != '-' && character != '.')
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">
    /// The route is not an app-relative path. Publishing is the boundary an untrusted platform
    /// callback crosses, so the shape is enforced here rather than at each head
    /// (SEC-Common-88 / SEC-ADC-65).
    /// </exception>
    public void Publish(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);

        if (!IsAppRelativeRoute(route))
        {
            throw new ArgumentException(
                "A deep-link route must be an app-relative path beginning with a single '/', with no scheme, backslash or control character.",
                nameof(route));
        }

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

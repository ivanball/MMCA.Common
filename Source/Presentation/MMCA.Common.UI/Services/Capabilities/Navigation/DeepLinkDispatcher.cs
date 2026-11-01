using System.Diagnostics.CodeAnalysis;

namespace MMCA.Common.UI.Services.Capabilities.Navigation;

/// <summary>
/// Default <see cref="IDeepLinkDispatcher"/>: raises <see cref="RouteRequested"/> when a
/// listener is attached, otherwise buffers the most recent route (capacity one) so a
/// cold-start tap survives until the Blazor router renders. Registered as a singleton:
/// native callers resolve it from the MAUI root service provider.
/// <list type="bullet">
/// <item><b>One listener.</b> A route goes to exactly one listener, the most recently attached one
/// still subscribed. A plain multicast event fanned one QR scan out to every <c>DeepLinkListener</c>
/// in the task (a recreated activity, a Custom Tab returning into the app leaves a second WebView),
/// so one scan became two sponsor-visit posts and the second answered "already visited" on a first
/// visit (A-27).</item>
/// <item><b>One delivery.</b> The same route published again within <see cref="DuplicateWindow"/>
/// is the same delivery seen twice (a launch intent republished on activity recreation, an
/// <c>OnNewIntent</c> for the intent that started the activity), so it is dropped rather than
/// navigated a second time.</item>
/// </list>
/// </summary>
public sealed class DeepLinkDispatcher : IDeepLinkDispatcher
{
    /// <summary>
    /// How long a published route counts as the same delivery. Short enough that a deliberate rescan
    /// of the same code still navigates; long enough to cover an activity recreation.
    /// </summary>
    internal static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private readonly List<EventHandler<DeepLinkRouteEventArgs>> _listeners = [];
    private string? _pendingRoute;
    private string? _lastRoute;
    private DateTimeOffset _lastRouteAt = DateTimeOffset.MinValue;

    /// <summary>Creates a dispatcher on the system clock.</summary>
    public DeepLinkDispatcher()
        : this(TimeProvider.System)
    {
    }

    /// <summary>Creates a dispatcher on the given clock.</summary>
    /// <param name="timeProvider">Clock for the duplicate window.</param>
    public DeepLinkDispatcher(TimeProvider timeProvider) =>
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <inheritdoc />
    /// <remarks>
    /// Explicit accessors keep the subscription order, so <see cref="Publish"/> can pick the newest
    /// listener instead of invoking every one. A listener subscribing again moves to the end.
    /// </remarks>
    public event EventHandler<DeepLinkRouteEventArgs>? RouteRequested
    {
        add
        {
            if (value is null)
            {
                return;
            }

            lock (_gate)
            {
                _listeners.Remove(value);
                _listeners.Add(value);
            }
        }

        remove
        {
            if (value is null)
            {
                return;
            }

            lock (_gate)
            {
                _listeners.Remove(value);
            }
        }
    }

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
    /// A <see langword="true"/> result also proves <paramref name="route"/> is not null, so a caller
    /// can pass it straight to <see cref="Publish"/> without a separate check.
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
    public static bool IsAppRelativeRoute([NotNullWhen(true)] string? route)
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
    /// Builds the in-app route for a link the platform head received, from the link's ENCODED parts,
    /// and applies <see cref="IsAppRelativeRoute"/> to the result. The link's scheme and host (the
    /// public web host, or a custom scheme's fixed host) are dropped: the path is the route.
    /// </summary>
    /// <param name="encodedPath">The link's path, still percent-encoded.</param>
    /// <param name="encodedQuery">The query without its leading <c>?</c>, still percent-encoded.</param>
    /// <param name="encodedFragment">The fragment without its leading <c>#</c>, still percent-encoded.</param>
    /// <param name="route">The route to publish, when the method answers <see langword="true"/>.</param>
    /// <returns>Whether the parts make an app-relative route that is safe to publish.</returns>
    /// <remarks>
    /// The parts stay encoded so a <c>%3F</c> or <c>%23</c> in the path cannot turn into a query or
    /// a fragment, and tokens in the query or fragment reach the page exactly as sent. The fragment
    /// is kept: a guest-order or confirm-email token rides in it. Asking here, rather than letting
    /// <see cref="Publish"/> throw, lets a native callback drop a hostile link silently (SEC-ADC-65).
    /// </remarks>
    public static bool TryComposeRoute(
        string? encodedPath,
        string? encodedQuery,
        string? encodedFragment,
        [NotNullWhen(true)] out string? route)
    {
        var candidate = encodedPath
            + (string.IsNullOrEmpty(encodedQuery) ? string.Empty : "?" + encodedQuery)
            + (string.IsNullOrEmpty(encodedFragment) ? string.Empty : "#" + encodedFragment);

        route = IsAppRelativeRoute(candidate) ? candidate : null;
        return route is not null;
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

        // Pick the listener INSIDE the lock: the duplicate check, the decision (raise now, or buffer
        // for the listener that has not attached yet) and the buffer write have to be one step.
        // Reading it outside allowed this interleaving, which drops the route: Publish sees no
        // listener, the listener subscribes, the listener drains an empty buffer, then Publish writes
        // into a buffer nobody will read again. That is the warm-boot deep link on a native head,
        // where the callback thread and the first render are genuinely concurrent.
        //
        // Under the lock the two orders are the only ones left. If the subscription was visible, the
        // event fires. If it was not, the buffer write completes before the lock is released, and the
        // listener's TryConsumePending, which must take the same lock afterwards, finds the route.
        EventHandler<DeepLinkRouteEventArgs> listener;
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (string.Equals(route, _lastRoute, StringComparison.Ordinal) && now - _lastRouteAt < DuplicateWindow)
            {
                return;
            }

            _lastRoute = route;
            _lastRouteAt = now;

            if (_listeners.Count == 0)
            {
                _pendingRoute = route;
                return;
            }

            listener = _listeners[^1];
        }

        // Invoked outside the lock: a listener that navigates on this callback must not run under it.
        listener.Invoke(this, new DeepLinkRouteEventArgs(route));
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

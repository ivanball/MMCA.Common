namespace MMCA.Common.UI.Web.Security;

/// <summary>
/// Stamps the trusted-internal-caller header on the outbound requests a server-rendered host sends
/// to its gateway, so those calls take the gateway's no-limiter partition instead of collapsing
/// every visitor into one client-IP window.
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem.</b> The gateway's edge limiter partitions on the connecting address, and an SSR
/// host makes all of its back-end calls from ONE address on behalf of every visitor: the render of a
/// page, and above all the cookie-session token refresh that runs whenever an access cookie has
/// expired. Those calls collapse into a single per-IP window, so the limiter starts throttling the
/// APPLICATION as soon as the site is busy rather than throttling a caller. A request proving
/// <c>GatewayRateLimiting:TrustedCallerSecret</c> in
/// <c>GatewayRateLimiting:TrustedCallerHeaderName</c> takes the no-limiter partition instead
/// (<see cref="MMCA.Common.Aspire.Gateway.GatewayRateLimitingSettings.TrustedCallerSecret"/>,
/// compared in constant time, single-valued header only).
/// </para>
/// <para>
/// <b>Why the breadth is safe.</b> The registration composes this handler onto EVERY client the host
/// creates, because the call that suffers most from the per-IP collapse is created by the framework
/// under a name a host cannot reach. Breadth costs nothing here because the handler itself is
/// narrow: it attaches the secret only to a request whose scheme, host and port match the gateway
/// origin, so a client later pointed at a third party (a telemetry exporter, an identity provider)
/// can never be handed the bypass by accident.
/// </para>
/// <para>
/// <b>Where the value belongs.</b> The secret exempts a component of the deployment, never an end
/// user's browser, so it must never reach client-side code: read it on the server from a secret
/// store or the environment (<c>GatewayRateLimiting__TrustedCallerSecret</c>), never from a
/// checked-in <c>appsettings</c> file.
/// </para>
/// </remarks>
public sealed class TrustedCallerHandler : DelegatingHandler
{
    private readonly string _headerName;
    private readonly string _secret;
    private readonly Uri _gatewayOrigin;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrustedCallerHandler"/> class.
    /// </summary>
    /// <param name="headerName">The header the gateway reads.</param>
    /// <param name="secret">The shared secret the gateway compares.</param>
    /// <param name="gatewayOrigin">
    /// The gateway origin. Only a request whose scheme, host and port match it is stamped.
    /// </param>
    public TrustedCallerHandler(string headerName, string secret, Uri gatewayOrigin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentNullException.ThrowIfNull(gatewayOrigin);

        _headerName = headerName;
        _secret = secret;
        _gatewayOrigin = gatewayOrigin;
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequestUri is { IsAbsoluteUri: true } uri
            && Uri.Compare(
                uri,
                _gatewayOrigin,
                UriComponents.Scheme | UriComponents.HostAndPort,
                UriFormat.UriEscaped,
                StringComparison.OrdinalIgnoreCase) == 0)
        {
            // Single-valued and replaced rather than appended: the gateway compares one header value
            // in constant time, so a second value would silently fail the comparison.
            request.Headers.Remove(_headerName);
            request.Headers.TryAddWithoutValidation(_headerName, _secret);
        }

        return base.SendAsync(request, cancellationToken);
    }
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace MMCA.Common.API.Startup;

/// <summary>
/// The framework's one forwarded-headers posture, shared by the service pipeline's
/// <c>ForwardedHeaders</c> step and by server-rendered UI hosts
/// (<see cref="ForwardedHeadersDefaultsExtensions.UseCommonUiForwardedHeaders"/>), so every host
/// behind the same ingress reads the scheme, host and client address the same way.
/// </summary>
/// <remarks>
/// <para>
/// <b>The known-proxy and known-network allow-lists are cleared on purpose.</b> Cloud reverse
/// proxies (Azure Container Apps ingress, AWS ALB and the like) reach the container from internal
/// addresses that are in neither default list, so leaving the defaults in place makes the middleware
/// ignore every forwarded header it receives: <c>Request.IsHttps</c> stays false behind TLS
/// termination, HTTPS redirection cannot resolve a port and no-ops, cookies lose the Secure flag
/// and every caller shares the ingress's IP for rate limiting. The posture assumes the ingress is
/// the only thing that can reach the container.
/// </para>
/// <para>
/// <c>MMCA.Common.Gateway</c> keeps its own dependency-free copy of the same values: the gateway
/// package does not reference this one.
/// </para>
/// </remarks>
public static class ForwardedHeadersDefaults
{
    /// <summary>
    /// The headers the framework honors by default: <c>X-Forwarded-For</c>,
    /// <c>X-Forwarded-Proto</c> and <c>X-Forwarded-Host</c>.
    /// </summary>
    public static ForwardedHeaders DefaultHeaders =>
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;

    /// <summary>
    /// Creates a fresh <see cref="ForwardedHeadersOptions"/> with the framework posture: the given
    /// header mask (or <see cref="DefaultHeaders"/>) and cleared known-proxy and known-network lists.
    /// </summary>
    /// <param name="headers">The headers to honor; <see langword="null"/> means <see cref="DefaultHeaders"/>.</param>
    /// <returns>A new options instance the caller owns.</returns>
    public static ForwardedHeadersOptions Create(ForwardedHeaders? headers = null)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = headers ?? DefaultHeaders,
        };

        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        return options;
    }
}

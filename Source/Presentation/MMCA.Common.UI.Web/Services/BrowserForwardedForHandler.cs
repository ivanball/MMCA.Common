using Microsoft.AspNetCore.Http;

namespace MMCA.Common.UI.Web.Services;

/// <summary>
/// Stamps <c>X-Forwarded-For</c> with the browser's address on the server-side <c>"APIClient"</c>
/// calls a Blazor Server host makes for a visitor (the SSR prerender and the interactive circuit), so
/// the API keys per-client policy (the registration rate limit, the session's recorded IP) on the
/// visitor rather than on this host's own address, which every visitor shares.
/// </summary>
/// <remarks>
/// <para>
/// <b>Same source and semantics as the cookie-session refresh.</b> The address is the
/// <see cref="ConnectionInfo.RemoteIpAddress"/> of the HTTP request that carries this work: the page
/// request during prerender, the connection the circuit was established on afterwards. That value
/// has already been through this host's forwarded-headers middleware, so it is only as far back as
/// this host trusts its own proxies; a client-supplied <c>X-Forwarded-For</c> is never copied
/// verbatim. The header is single-valued and replaces any value already on the request.
/// </para>
/// <para>
/// <b>Server only.</b> Registered by <c>AddCommonServerTokenStorage()</c>, which only a Blazor
/// Server host calls; the WebAssembly client's calls reach the API through the same-origin proxy,
/// which stamps the header itself. When no request is in scope (a background call with no visitor
/// behind it) nothing is sent.
/// </para>
/// </remarks>
/// <param name="httpContextAccessor">Reads the request in scope; an async-local, so it crosses the
/// handler's own DI scope.</param>
internal sealed class BrowserForwardedForHandler(IHttpContextAccessor httpContextAccessor) : DelegatingHandler
{
    /// <summary>The header the API's forwarded-headers configuration reads.</summary>
    internal const string HeaderName = "X-Forwarded-For";

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var remoteIpAddress = httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
        if (remoteIpAddress is not null)
        {
            request.Headers.Remove(HeaderName);
            request.Headers.TryAddWithoutValidation(HeaderName, remoteIpAddress);
        }

        return base.SendAsync(request, cancellationToken);
    }
}

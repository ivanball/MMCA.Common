using Microsoft.AspNetCore.Http;

namespace MMCA.Common.UI.Web.Services;

/// <summary>
/// Stamps the browser's origin (<c>X-Forwarded-For</c> with its address, <c>User-Agent</c> with its
/// user-agent) on the server-side <c>"APIClient"</c> calls a Blazor Server host makes for a visitor
/// (the SSR prerender and the interactive circuit), so the API keys per-client policy (the
/// registration rate limit, the session's recorded IP and device) on the visitor rather than on this
/// host, whose own address every visitor shares and whose HTTP client sends no user-agent at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Same source and semantics as the cookie-session refresh.</b> Both values come from the HTTP
/// request that carries this work: the page request during prerender, the connection the circuit was
/// established on afterwards. The address is its <see cref="ConnectionInfo.RemoteIpAddress"/>, which
/// has already been through this host's forwarded-headers middleware, so it is only as far back as
/// this host trusts its own proxies; a client-supplied <c>X-Forwarded-For</c> is never copied
/// verbatim. The user-agent is informational only (it names the device on the signed-in devices
/// page), so it is forwarded as the browser sent it. Each header is single-valued and replaces any
/// value already on the request; a blank user-agent is not forwarded.
/// </para>
/// <para>
/// <b>Server only.</b> Registered by <c>AddCommonServerTokenStorage()</c>, which only a Blazor
/// Server host calls; the WebAssembly client's calls reach the API through the same-origin proxy,
/// which forwards the browser's own headers. When no request is in scope (a background call with no
/// visitor behind it) nothing is sent.
/// </para>
/// </remarks>
/// <param name="httpContextAccessor">Reads the request in scope; an async-local, so it crosses the
/// handler's own DI scope.</param>
internal sealed class BrowserOriginHandler(IHttpContextAccessor httpContextAccessor) : DelegatingHandler
{
    /// <summary>The header the API's forwarded-headers configuration reads.</summary>
    internal const string ForwardedForHeaderName = "X-Forwarded-For";

    /// <summary>The header the API records as the session's device.</summary>
    internal const string UserAgentHeaderName = "User-Agent";

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is not null)
        {
            Replace(request, ForwardedForHeaderName, httpContext.Connection.RemoteIpAddress?.ToString());
            Replace(request, UserAgentHeaderName, httpContext.Request.Headers.UserAgent.ToString());
        }

        return base.SendAsync(request, cancellationToken);
    }

    private static void Replace(HttpRequestMessage request, string headerName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        // TryAddWithoutValidation: a real browser user-agent does not always parse as a strict
        // product token list, and neither value needs parsing on this side.
        request.Headers.Remove(headerName);
        request.Headers.TryAddWithoutValidation(headerName, value);
    }
}

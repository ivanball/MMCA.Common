using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.Aspire.Security;
using MMCA.Common.UI.Common.Settings;

namespace MMCA.Common.UI.Web.Security;

/// <summary>
/// Supplies a Blazor Web host's Content-Security-Policy to the shared
/// <see cref="SecurityHeadersMiddleware"/>. <c>connect-src</c> is pinned to <c>'self'</c> plus the
/// configured API/Gateway origin (https + wss) from <see cref="ApiSettings"/>
/// (<c>WasmApiEndpoint</c> ?? <c>ApiEndpoint</c>): <c>'self'</c> covers the Blazor Server interactive
/// circuit's same-origin WebSocket; the Gateway origin covers cross-origin API calls and the SignalR
/// notification hub. If the endpoint cannot be resolved/parsed, the CSP degrades to a permissive
/// <c>Report-Only</c> policy so a misconfiguration can never hard-break the app in production.
/// Hoisted from the app Blazor Web hosts (byte-identical there); register via
/// <c>AddCommonBlazorCsp()</c> BEFORE <c>AddCommonSecurityHeaders</c>.
/// </summary>
internal sealed class BlazorCspPolicyProvider : ICspPolicyProvider
{
    // Computed once (registered as a singleton): the full CSP value and whether to enforce it.
    private readonly CspPolicy _policy;

    public BlazorCspPolicyProvider(IOptions<ApiSettings> apiOptions, IWebHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(apiOptions);
        ArgumentNullException.ThrowIfNull(environment);
        _policy = BuildCsp(apiOptions.Value, environment.IsDevelopment());
    }

    /// <inheritdoc />
    public CspPolicy? GetPolicy(HttpContext context) => _policy;

    // Builds the CSP. When the API/Gateway origin can be pinned, the policy is enforced; otherwise it
    // degrades to a permissive Report-Only policy (never enforce a CSP we can't construct correctly).
    private static CspPolicy BuildCsp(ApiSettings api, bool isDevelopment)
    {
        var endpoint = api.WasmApiEndpoint ?? api.ApiEndpoint;

        if (string.IsNullOrWhiteSpace(endpoint) ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var apiUri))
        {
            return new CspPolicy(BuildPolicy("connect-src 'self' https: wss:", isDevelopment), Enforce: false);
        }

        // scheme://host:port for the API/Gateway, plus its WebSocket origin (SignalR notification hub).
        var origin = apiUri.GetLeftPart(UriPartial.Authority);
        var wsScheme = string.Equals(apiUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        var connectSrc = $"connect-src 'self' {origin} {wsScheme}://{apiUri.Authority}";

        // Visual Studio's Browser Link + Hot Reload inject an inline bootstrap script and open a WebSocket
        // to a dynamically-assigned localhost port (e.g. ws://localhost:52038, http://localhost:52040) that
        // changes every run. Allow those localhost origins in Development only so the hardened production CSP
        // is never loosened; script-src gains 'unsafe-inline' for the same reason (see BuildPolicy).
        if (isDevelopment)
        {
            connectSrc += " http://localhost:* ws://localhost:*";
        }

        return new CspPolicy(BuildPolicy(connectSrc, isDevelopment), Enforce: true);
    }

    // img-src allows any https source: profile pictures and other content images come from arbitrary
    // external hosts (CDNs/user-supplied URLs). Images are low XSS-risk; the directives that matter
    // for exfiltration — script-src and connect-src — stay locked down. In Development only, script-src
    // also permits 'unsafe-inline' so Visual Studio's injected Hot Reload bootstrap script can run.
    private static string BuildPolicy(string connectSrc, bool isDevelopment) =>
        "default-src 'self'; " +
        $"script-src 'self' 'wasm-unsafe-eval'{(isDevelopment ? " 'unsafe-inline'" : string.Empty)}; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: https:; " +
        "font-src 'self'; " +
        connectSrc + "; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";
}

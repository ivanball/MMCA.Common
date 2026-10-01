using System.Net;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using MMCA.Common.API.SessionCookies;
using MMCA.Common.UI.Services.Auth;
using Yarp.ReverseProxy.Forwarder;

namespace MMCA.Common.UI.Web.SameOriginProxy;

/// <summary>
/// The request delegate behind <c>{PathPrefix}/{**path}</c>. In order: the CSRF gate (unsafe methods
/// must carry <c>X-CSRF: 1</c>), the locally answered refresh, the session step (validate-or-refresh
/// the cookie's access token, single-flighted per session by <see cref="ICookieSessionRefresher"/>; a
/// session that can no longer refresh is cleared and answered 401), the forward through YARP with the
/// bearer attached server-side, and, for a safe method answered 401, one forced refresh and replay.
/// Token-issuing and sign-out endpoints get their response treatment from
/// <see cref="SameOriginProxyTransformer"/>.
/// </summary>
internal sealed partial class SameOriginApiProxyEndpoint(
    IHttpForwarder forwarder,
    SameOriginProxyInvoker invoker,
    ICookieSessionRefresher refresher,
    ISessionCookieStore cookieStore,
    IOptions<SameOriginApiProxySettings> settings,
    ILogger<SameOriginApiProxyEndpoint> logger)
{
    // HTTP/1.1 upstream: WebSocket upgrades forward as plain upgrades, and nothing here needs HTTP/2.
    private static readonly ForwarderRequestConfig RequestConfig = new()
    {
        ActivityTimeout = TimeSpan.FromSeconds(100),
        Version = HttpVersion.Version11,
        VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
    };

    private readonly SameOriginApiProxySettings _settings = settings.Value;
    private readonly HashSet<string> _tokenIssuingPaths = new(
        SameOriginApiProxySettings.DefaultTokenIssuingPaths
            .Concat(settings.Value.AdditionalTokenIssuingPaths)
            .Select(Normalize),
        StringComparer.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A proxied 401/404 with an empty body must reach the client as sent, not as the host's
        // re-executed HTML status page.
        if (context.Features.Get<IStatusCodePagesFeature>() is { } statusCodePages)
        {
            statusCodePages.Enabled = false;
        }

        if (!IsSafeMethod(context.Request.Method) && !HasCsrfHeader(context.Request))
        {
            LogCsrfRejected(logger, context.Request.Method);
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "csrf_header_required").ConfigureAwait(false);
            return;
        }

        var mode = ResolveMode(context.Request.Method, Normalize(RemainingPath(context)));
        if (mode is null)
        {
            await RefreshLocallyAsync(context).ConfigureAwait(false);
            return;
        }

        string? bearer = null;

        // A sign-in is anonymous by definition, so a stale session must not block it.
        if (mode != ProxyResponseMode.TokenIssuing && HasSessionCookie(context.Request))
        {
            var session = await refresher.GetOrRefreshAsync(context, context.RequestAborted).ConfigureAwait(false);
            if (session is null)
            {
                await EndSessionAsync(context).ConfigureAwait(false);
                return;
            }

            bearer = session.Value.AccessToken;
        }

        var first = CreateTransformer(bearer, mode.Value, captureUnauthorized: bearer is not null && IsReplayable(context.Request));
        await ForwardAsync(context, first).ConfigureAwait(false);

        if (first.UnauthorizedCaptured)
        {
            await RefreshAndReplayAsync(context, mode.Value).ConfigureAwait(false);
        }
    }

    internal static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);

    internal static bool HasCsrfHeader(HttpRequest request) =>
        request.Headers.TryGetValue(SameOriginProxyHeaders.CsrfHeaderName, out var value)
        && value.Count == 1
        && string.Equals(value[0], SameOriginProxyHeaders.CsrfHeaderValue, StringComparison.Ordinal);

    private static bool HasSessionCookie(HttpRequest request) =>
        !string.IsNullOrEmpty(request.Cookies[SessionCookieEndpoints.AccessTokenCookieName])
        || !string.IsNullOrEmpty(request.Cookies[SessionCookieEndpoints.RefreshTokenCookieName]);

    // GET/HEAD/OPTIONS carry no body to replay; an upgrade has already handed its connection over.
    private static bool IsReplayable(HttpRequest request) =>
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method))
        && !request.Headers.ContainsKey(HeaderNames.Upgrade);

    private static string Normalize(string path) => path.Trim('/');

    private static Task WriteErrorAsync(HttpContext context, int statusCode, string error)
    {
        context.Response.StatusCode = statusCode;
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsJsonAsync(new { error }, context.RequestAborted);
    }

    /// <summary>A session that can no longer be refreshed: clear its cookies and answer 401.</summary>
    private Task EndSessionAsync(HttpContext context)
    {
        cookieStore.Clear(context);
        return WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "session_expired");
    }

    /// <summary>
    /// The response treatment for a request, or <see langword="null"/> for the refresh endpoint, which
    /// the proxy answers itself. Only POSTs are special: every other method is a plain forward.
    /// </summary>
    private ProxyResponseMode? ResolveMode(string method, string relativePath)
    {
        if (!HttpMethods.IsPost(method))
        {
            return ProxyResponseMode.Forward;
        }

        if (string.Equals(relativePath, Normalize(_settings.RefreshPath), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (_tokenIssuingPaths.Contains(relativePath))
        {
            return ProxyResponseMode.TokenIssuing;
        }

        return string.Equals(relativePath, Normalize(_settings.RevokePath), StringComparison.OrdinalIgnoreCase)
            ? ProxyResponseMode.Revoke
            : ProxyResponseMode.Forward;
    }

    /// <summary>
    /// The upstream rejected a token that still looked valid (revoked, or a rotated signing key): one
    /// forced refresh, one replay. Only safe, bodiless methods get here, so nothing is re-sent that the
    /// upstream could apply twice.
    /// </summary>
    private async Task RefreshAndReplayAsync(HttpContext context, ProxyResponseMode mode)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        var refreshed = await refresher.RefreshAsync(context, context.RequestAborted).ConfigureAwait(false);
        if (refreshed is null)
        {
            await EndSessionAsync(context).ConfigureAwait(false);
            return;
        }

        await ForwardAsync(context, CreateTransformer(refreshed.Value.AccessToken, mode, captureUnauthorized: false)).ConfigureAwait(false);
    }

    private string RemainingPath(HttpContext context) =>
        context.Request.Path.StartsWithSegments(_settings.PathPrefix, StringComparison.OrdinalIgnoreCase, out var remaining)
            ? remaining.Value ?? string.Empty
            : context.Request.Path.Value ?? string.Empty;

    private SameOriginProxyTransformer CreateTransformer(string? bearer, ProxyResponseMode mode, bool captureUnauthorized) =>
        new(_settings.PathPrefix, bearer, mode, captureUnauthorized, cookieStore);

    private async Task ForwardAsync(HttpContext context, SameOriginProxyTransformer transformer)
    {
        var error = await forwarder.SendAsync(
            context, _settings.GatewayAddress!, invoker.Invoker, RequestConfig, transformer, context.RequestAborted).ConfigureAwait(false);

        // A browser that navigated away or closed a socket is not a gateway problem.
        if (error != ForwarderError.None && !context.RequestAborted.IsCancellationRequested)
        {
            LogForwardFailed(logger, error, context.Features.Get<IForwarderErrorFeature>()?.Exception);
        }
    }

    /// <summary>
    /// <c>POST {prefix}/auth/refresh</c> from the browser: the refresh token is in the HttpOnly cookie,
    /// never in the request, so the proxy rotates it itself and answers in the sign-in response shape
    /// with the tokens stripped.
    /// </summary>
    private async Task RefreshLocallyAsync(HttpContext context)
    {
        var refreshed = await refresher.RefreshAsync(context, context.RequestAborted).ConfigureAwait(false);
        if (refreshed is null)
        {
            await EndSessionAsync(context).ConfigureAwait(false);
            return;
        }

        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(
            new
            {
                AccessToken = SessionClaimsToken.Create(refreshed.Value.AccessToken) ?? string.Empty,
                RefreshToken = string.Empty,
                refreshed.Value.AccessTokenExpiry,
            },
            context.RequestAborted).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Same-origin API proxy rejected a {Method} request without the CSRF header")]
    private static partial void LogCsrfRejected(ILogger logger, string method);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Same-origin API proxy could not forward the request to the gateway: {Error}")]
    private static partial void LogForwardFailed(ILogger logger, ForwarderError error, Exception? exception);
}

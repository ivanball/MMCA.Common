using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using MMCA.Common.API.SessionCookies;
using MMCA.Common.UI.Services.Auth;
using Yarp.ReverseProxy.Forwarder;

namespace MMCA.Common.UI.Web.SameOriginProxy;

/// <summary>
/// The request delegate behind <c>{PathPrefix}/{**path}</c>. In order: the same-origin gate (a foreign
/// <c>Origin</c> or <c>Sec-Fetch-Site</c> is refused, and a WebSocket upgrade must carry this host's own
/// <c>Origin</c>), the local answer to <c>OPTIONS</c> (never forwarded, never a CORS grant), the CSRF
/// gate (unsafe methods must carry <c>X-CSRF: 1</c>; the one exemption is a WebSocket opened over
/// HTTP/2, an RFC 8441 extended <c>CONNECT</c> to which a browser cannot add headers, and which the
/// same-origin gate has already held to this host's own <c>Origin</c>), the locally answered refresh, the session step
/// (validate-or-refresh the cookie's access token, single-flighted per session by
/// <see cref="ICookieSessionRefresher"/>; a session whose refresh token was refused is cleared and
/// answered 401, while a refresh that could not be decided right now keeps the cookies and is answered
/// 503 with <c>Retry-After</c>), the forward through YARP with the bearer attached server-side, and,
/// for a safe method answered 401, one forced refresh and replay. Token-issuing and sign-out endpoints
/// get their response treatment from <see cref="SameOriginProxyTransformer"/>.
/// </summary>
internal sealed partial class SameOriginApiProxyEndpoint(
    IHttpForwarder forwarder,
    SameOriginProxyInvoker invoker,
    ICookieSessionRefresher refresher,
    ISessionCookieStore cookieStore,
    IOptions<SameOriginApiProxySettings> settings,
    ILogger<SameOriginApiProxyEndpoint> logger)
{
    private const string SecFetchSiteHeaderName = "Sec-Fetch-Site";
    private const string WebSocketProtocol = "websocket";

    // HTTP/1.1 upstream, and nothing here needs HTTP/2: an HTTP/1.1 WebSocket upgrade forwards as a
    // plain upgrade, and an HTTP/2 one (an extended CONNECT from the browser) is turned by YARP into an
    // HTTP/1.1 GET upgrade to the gateway, which this version cap is what permits.
    private static readonly ForwarderRequestConfig RequestConfig = new()
    {
        ActivityTimeout = TimeSpan.FromSeconds(100),
        Version = HttpVersion.Version11,
        VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
    };

    // Used when the identity endpoint gave no Retry-After of its own.
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(5);

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

        if (await TryAnswerBeforeForwardingAsync(context).ConfigureAwait(false))
        {
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
            var outcome = await refresher.ValidateOrRefreshAsync(context, context.RequestAborted).ConfigureAwait(false);
            if (outcome.Session is not { } session)
            {
                await FailRefreshAsync(context, outcome).ConfigureAwait(false);
                return;
            }

            bearer = session.AccessToken;
        }

        var first = CreateTransformer(bearer, mode.Value, captureUnauthorized: bearer is not null && IsReplayable(context));
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

    /// <summary>
    /// The proxy serves only this host's own pages, so it refuses anything a browser marks as coming
    /// from elsewhere, before the CSRF gate and before anything is forwarded. Returns why a request is
    /// refused, or <see langword="null"/> to let it through:
    /// <list type="bullet">
    /// <item>an <c>Origin</c> header that is not exactly this host's origin (a same-site sibling such
    /// as another subdomain is still another origin);</item>
    /// <item>a WebSocket upgrade without an <c>Origin</c> (browsers always send one, and an upgrade is
    /// not CORS-protected, so the origin is the only proof of who opened it), whether it is an HTTP/1.1
    /// GET upgrade or an HTTP/2 extended <c>CONNECT</c> (<see cref="IsWebSocketExtendedConnect"/>);</item>
    /// <item>a <c>Sec-Fetch-Site</c> other than <c>same-origin</c>, except <c>none</c> (a user-initiated
    /// navigation, such as a pasted download link) on a plain GET or HEAD.</item>
    /// </list>
    /// A request with neither header (a same-origin GET, a non-browser caller) passes; unsafe methods
    /// still need the CSRF header after this.
    /// </summary>
    internal static string? CrossOriginRejection(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var upgrade = IsUpgrade(context);
        return OriginRejection(context.Request, upgrade) ?? FetchSiteRejection(context.Request, upgrade);
    }

    /// <summary>
    /// Whether <paramref name="origin"/> is this host's own origin: the request's scheme, host and port
    /// as the app sees them, which is after <c>UseForwardedHeaders</c> (<c>UseCommonUiForwardedHeaders</c>)
    /// has applied <c>X-Forwarded-Proto</c>/<c>X-Forwarded-Host</c> behind a reverse proxy. Default ports
    /// compare equal to an omitted port; anything that is not a bare origin (a path, user info, the
    /// opaque origin a browser serializes as the string "null") does not match.
    /// </summary>
    internal static bool IsOwnOrigin(HttpRequest request, string? origin)
    {
        if (string.IsNullOrEmpty(origin)
            || !request.Host.HasValue
            || !Uri.TryCreate(origin, UriKind.Absolute, out var candidate)
            || candidate.UserInfo.Length != 0
            || candidate.PathAndQuery != "/"
            || candidate.Fragment.Length != 0
            || !Uri.TryCreate(request.Scheme + Uri.SchemeDelimiter + request.Host.ToUriComponent(), UriKind.Absolute, out var own))
        {
            return false;
        }

        return Uri.Compare(candidate, own, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
    }

    /// <summary>
    /// Whether the request is a browser opening a WebSocket over HTTP/2: an RFC 8441 extended
    /// <c>CONNECT</c> with <c>:protocol websocket</c> and no <c>Upgrade</c> header. It is treated like the
    /// HTTP/1.1 GET upgrade everywhere: same-origin <c>Origin</c> required, exempt from the CSRF header
    /// (a browser cannot add one to a WebSocket), never replayed. Any other <c>CONNECT</c> is an
    /// ordinary unsafe method.
    /// </summary>
    internal static bool IsWebSocketExtendedConnect(HttpContext context) =>
        HttpMethods.IsConnect(context.Request.Method)
        && context.Features.Get<IHttpExtendedConnectFeature>() is { IsExtendedConnect: true } connect
        && string.Equals(connect.Protocol, WebSocketProtocol, StringComparison.OrdinalIgnoreCase);

    // An HTTP/1.1 upgrade (with or without UseWebSockets registered) or an HTTP/2 extended CONNECT.
    private static bool IsUpgrade(HttpContext context) =>
        context.WebSockets.IsWebSocketRequest
        || context.Request.Headers.ContainsKey(HeaderNames.Upgrade)
        || IsWebSocketExtendedConnect(context);

    private static string? OriginRejection(HttpRequest request, bool upgrade)
    {
        if (!request.Headers.TryGetValue(HeaderNames.Origin, out var origin))
        {
            return upgrade ? "WebSocket upgrade without Origin" : null;
        }

        return origin.Count == 1 && IsOwnOrigin(request, origin[0]) ? null : "foreign Origin";
    }

    private static string? FetchSiteRejection(HttpRequest request, bool upgrade)
    {
        if (!request.Headers.TryGetValue(SecFetchSiteHeaderName, out var fetchSite))
        {
            return null;
        }

        var value = fetchSite.Count == 1 ? fetchSite[0] : null;
        if (string.Equals(value, "same-origin", StringComparison.Ordinal))
        {
            return null;
        }

        var userInitiatedNavigation = string.Equals(value, "none", StringComparison.Ordinal)
            && !upgrade
            && (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method));
        return userInitiatedNavigation ? null : "Sec-Fetch-Site " + (value ?? "(multiple)");
    }

    private static bool HasSessionCookie(HttpRequest request) =>
        !string.IsNullOrEmpty(request.Cookies[SessionCookieEndpoints.AccessTokenCookieName])
        || !string.IsNullOrEmpty(request.Cookies[SessionCookieEndpoints.RefreshTokenCookieName]);

    // GET/HEAD/OPTIONS carry no body to replay; an upgrade (HTTP/1.1 or HTTP/2) has already handed its
    // connection over.
    private static bool IsReplayable(HttpContext context) =>
        (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) || HttpMethods.IsOptions(context.Request.Method))
        && !IsUpgrade(context);

    private static string Normalize(string path) => path.Trim('/');

    private static Task WriteErrorAsync(HttpContext context, int statusCode, string error)
    {
        context.Response.StatusCode = statusCode;
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsJsonAsync(new { error }, context.RequestAborted);
    }

    /// <summary>
    /// The gates and local answers that run before anything is forwarded, in order: the same-origin
    /// gate, <c>OPTIONS</c>, the CSRF gate. Returns <see langword="true"/> when the response is written.
    /// </summary>
    private async Task<bool> TryAnswerBeforeForwardingAsync(HttpContext context)
    {
        if (CrossOriginRejection(context) is { } rejection)
        {
            LogCrossOriginRejected(logger, context.Request.Method, rejection);
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "cross_origin_rejected").ConfigureAwait(false);
            return true;
        }

        // Never forwarded: a same-origin page needs no preflight, and a cross-origin one was refused
        // above, so the gateway's CORS policy is never consulted on the proxy's behalf. No CORS grant.
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            context.Response.Headers.CacheControl = "no-store";
            return true;
        }

        // An HTTP/2 WebSocket cannot carry the header; the same-origin gate above required its Origin.
        if (!IsSafeMethod(context.Request.Method) && !IsWebSocketExtendedConnect(context) && !HasCsrfHeader(context.Request))
        {
            LogCsrfRejected(logger, context.Request.Method);
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "csrf_header_required").ConfigureAwait(false);
            return true;
        }

        return false;
    }

    /// <summary>A session that can no longer be refreshed: clear its cookies and answer 401.</summary>
    private Task EndSessionAsync(HttpContext context)
    {
        cookieStore.Clear(context);
        return WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "session_expired");
    }

    /// <summary>
    /// A refresh that produced no token. Only a refused refresh token ends the session; a refresh that
    /// could not be decided (identity endpoint down, throttled, timed out) keeps the cookies, forwards
    /// and replays nothing, and answers 503 with the upstream's <c>Retry-After</c> (or a short default),
    /// so a blip at the identity endpoint does not sign the user out.
    /// </summary>
    private Task FailRefreshAsync(HttpContext context, SessionRefreshOutcome outcome)
    {
        if (outcome.Status == SessionRefreshStatus.Rejected)
        {
            return EndSessionAsync(context);
        }

        LogRefreshUnavailable(logger);
        var retryAfter = outcome.RetryAfter ?? DefaultRetryAfter;
        context.Response.Headers.RetryAfter = ((long)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "session_refresh_unavailable");
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
        var outcome = await refresher.RefreshAsync(context, context.RequestAborted).ConfigureAwait(false);
        if (outcome.Session is not { } refreshed)
        {
            await FailRefreshAsync(context, outcome).ConfigureAwait(false);
            return;
        }

        await ForwardAsync(context, CreateTransformer(refreshed.AccessToken, mode, captureUnauthorized: false)).ConfigureAwait(false);
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
        var outcome = await refresher.RefreshAsync(context, context.RequestAborted).ConfigureAwait(false);
        if (outcome.Session is not { } refreshed)
        {
            await FailRefreshAsync(context, outcome).ConfigureAwait(false);
            return;
        }

        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(
            new
            {
                AccessToken = SessionClaimsToken.Create(refreshed.AccessToken) ?? string.Empty,
                RefreshToken = string.Empty,
                refreshed.AccessTokenExpiry,
            },
            context.RequestAborted).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Same-origin API proxy rejected a {Method} request without the CSRF header")]
    private static partial void LogCsrfRejected(ILogger logger, string method);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Same-origin API proxy rejected a cross-origin {Method} request ({Reason})")]
    private static partial void LogCrossOriginRejected(ILogger logger, string method, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Same-origin API proxy could not refresh the session right now; the cookies are kept and the request is answered 503")]
    private static partial void LogRefreshUnavailable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Same-origin API proxy could not forward the request to the gateway: {Error}")]
    private static partial void LogForwardFailed(ILogger logger, ForwarderError error, Exception? exception);
}

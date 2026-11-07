using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using MMCA.Common.API.SessionCookies;
using MMCA.Common.UI.Services.Auth;
using Yarp.ReverseProxy.Forwarder;

namespace MMCA.Common.UI.Web.SameOriginProxy;

/// <summary>What the proxy does with one request's upstream response beyond copying it.</summary>
internal enum ProxyResponseMode
{
    /// <summary>Copy the response as is.</summary>
    Forward,

    /// <summary>A sign-in: move the token pair into the session cookies and strip it from the body.</summary>
    TokenIssuing,

    /// <summary>A sign-out: copy the response, then clear the session cookies.</summary>
    Revoke,

    /// <summary>
    /// A view-as grant (ADR-137): move the token into the overlay cookie and strip it from the body;
    /// the session cookies are not touched.
    /// </summary>
    ViewAsGrant,

    /// <summary>End of a view-as session: copy the response, then delete the overlay cookie.</summary>
    ViewAsEnd,
}

/// <summary>What one forwarded request does with the view-as overlay cookie (ADR-137).</summary>
internal readonly record struct ViewAsOverlayTreatment(bool ForwardsOverlayBearer, bool DeleteOverlay, Microsoft.AspNetCore.Http.SameSiteMode SameSite);

/// <summary>
/// One request's YARP transform. Starts from <see cref="HttpTransformer.Default"/> (X-Forwarded-*
/// headers, upstream Host) and then: maps <c>{prefix}/rest</c> to <c>{gateway}/rest</c>, replaces
/// whatever <c>Authorization</c> the browser sent with the session's bearer (or none), keeps the session
/// cookies and the CSRF header off the upstream request, and applies the <see cref="ProxyResponseMode"/>.
/// When <c>captureUnauthorized</c> is set, an upstream 401 is swallowed (nothing copied) so the caller
/// can refresh and replay the request once. The view-as overlay cookie (ADR-137) is written from a
/// grant response and deleted on end, sign-out, sign-in, an expired overlay, and an upstream 401 to the
/// overlay's bearer. Not shared: it carries per-request state.
/// </summary>
internal sealed class SameOriginProxyTransformer(
    PathString pathPrefix,
    string? bearerToken,
    ProxyResponseMode mode,
    bool captureUnauthorized,
    ISessionCookieStore cookieStore,
    ViewAsOverlayTreatment overlay = default) : HttpTransformer
{
    private static readonly string[] SessionCookieNames =
        [SessionCookieEndpoints.AccessTokenCookieName, SessionCookieEndpoints.RefreshTokenCookieName, ViewAsOverlayCookie.Name];

    /// <summary>Gets a value indicating whether an upstream 401 was swallowed for a replay.</summary>
    public bool UnauthorizedCaptured { get; private set; }

    public override async ValueTask TransformRequestAsync(
        HttpContext httpContext,
        HttpRequestMessage proxyRequest,
        string destinationPrefix,
        CancellationToken cancellationToken)
    {
        await Default.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken).ConfigureAwait(false);

        var path = httpContext.Request.Path.StartsWithSegments(pathPrefix, StringComparison.OrdinalIgnoreCase, out var remaining)
            ? remaining
            : httpContext.Request.Path;
        proxyRequest.RequestUri = RequestUtilities.MakeDestinationAddress(destinationPrefix, path, httpContext.Request.QueryString);

        proxyRequest.Headers.Authorization = bearerToken is null ? null : new AuthenticationHeaderValue("Bearer", bearerToken);
        proxyRequest.Headers.Remove(SameOriginProxyHeaders.CsrfHeaderName);
        RemoveSessionCookies(proxyRequest.Headers);

        if (mode is ProxyResponseMode.TokenIssuing or ProxyResponseMode.ViewAsGrant)
        {
            // The body is rewritten here, so it must arrive as plain JSON.
            proxyRequest.Headers.AcceptEncoding.Clear();
        }
    }

    public override async ValueTask<bool> TransformResponseAsync(
        HttpContext httpContext,
        HttpResponseMessage? proxyResponse,
        CancellationToken cancellationToken)
    {
        if (captureUnauthorized && proxyResponse?.StatusCode == HttpStatusCode.Unauthorized)
        {
            UnauthorizedCaptured = true;
            return false;
        }

        var copyBody = await Default.TransformResponseAsync(httpContext, proxyResponse, cancellationToken).ConfigureAwait(false);

        if (ShouldDeleteOverlay(proxyResponse))
        {
            ViewAsOverlayCookie.Delete(httpContext, overlay.SameSite);
        }

        if (mode == ProxyResponseMode.Revoke)
        {
            // Signed out whatever the upstream said: a failed revoke must not strand the user in a session.
            cookieStore.Clear(httpContext);
            return copyBody;
        }

        if (mode == ProxyResponseMode.TokenIssuing && proxyResponse is { IsSuccessStatusCode: true })
        {
            await RewriteTokenResponseAsync(httpContext, proxyResponse, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (mode == ProxyResponseMode.ViewAsGrant && proxyResponse is { IsSuccessStatusCode: true })
        {
            await RewriteViewAsResponseAsync(httpContext, proxyResponse, cancellationToken).ConfigureAwait(false);
            return false;
        }

        return copyBody;
    }

    /// <summary>
    /// The overlay goes when the view ends (whatever the upstream said, like a sign-out), when the
    /// staff user signs out or anyone signs in, when it had already expired, and when the upstream
    /// refused its bearer (an ended or expired grant): a view-as token is never refreshed or replayed.
    /// </summary>
    private bool ShouldDeleteOverlay(HttpResponseMessage? proxyResponse) =>
        overlay.DeleteOverlay
        || mode is ProxyResponseMode.ViewAsEnd or ProxyResponseMode.Revoke
        || mode == ProxyResponseMode.TokenIssuing && proxyResponse is { IsSuccessStatusCode: true }
        || overlay.ForwardsOverlayBearer && proxyResponse?.StatusCode == HttpStatusCode.Unauthorized;

    /// <summary>
    /// Moves a view-as grant response's token into the overlay cookie and sends the browser the same
    /// JSON with <c>accessToken</c> replaced by its claims-only form. The session cookies are left
    /// alone, so the staff user's own session survives the view. A body with no readable token passes
    /// through unchanged.
    /// </summary>
    private async Task RewriteViewAsResponseAsync(HttpContext httpContext, HttpResponseMessage proxyResponse, CancellationToken cancellationToken)
    {
        var body = await proxyResponse.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var rewritten = TryMoveViewAsTokenToOverlay(httpContext, body);

        var response = httpContext.Response;
        response.Headers.ContentLength = null;
        if (rewritten is not null)
        {
            response.Headers.CacheControl = "no-store";
            response.Headers.Pragma = "no-cache";
        }

        await response.Body.WriteAsync(rewritten ?? body, cancellationToken).ConfigureAwait(false);
    }

    private byte[]? TryMoveViewAsTokenToOverlay(HttpContext httpContext, byte[] body)
    {
        JsonObject? json;
        try
        {
            json = JsonNode.Parse(body, new JsonNodeOptions { PropertyNameCaseInsensitive = true }) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        var accessToken = ReadString(json, "accessToken");
        var claimsOnly = SessionClaimsToken.Create(accessToken);
        if (json is null || accessToken is null || claimsOnly is null)
        {
            return null;
        }

        ViewAsOverlayCookie.Write(httpContext, accessToken, new JwtSecurityTokenHandler().ReadJwtToken(accessToken).ValidTo, overlay.SameSite);
        json["accessToken"] = claimsOnly;
        return JsonSerializer.SerializeToUtf8Bytes(json);
    }

    /// <summary>
    /// Moves a sign-in response's token pair into the session cookies and sends the browser the same
    /// JSON with <c>accessToken</c> replaced by its claims-only form and <c>refreshToken</c> emptied, so
    /// a client written for the default mode still reads a well-formed response. A body that carries no
    /// token pair is passed through unchanged.
    /// </summary>
    private async Task RewriteTokenResponseAsync(HttpContext httpContext, HttpResponseMessage proxyResponse, CancellationToken cancellationToken)
    {
        var body = await proxyResponse.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var rewritten = TryMoveTokensToCookies(httpContext, body);

        var response = httpContext.Response;
        response.Headers.ContentLength = null;
        if (rewritten is not null)
        {
            response.Headers.CacheControl = "no-store";
            response.Headers.Pragma = "no-cache";
        }

        await response.Body.WriteAsync(rewritten ?? body, cancellationToken).ConfigureAwait(false);
    }

    private byte[]? TryMoveTokensToCookies(HttpContext httpContext, byte[] body)
    {
        JsonObject? json;
        try
        {
            json = JsonNode.Parse(body, new JsonNodeOptions { PropertyNameCaseInsensitive = true }) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        var accessToken = ReadString(json, "accessToken");
        var refreshToken = ReadString(json, "refreshToken");
        if (json is null || accessToken is null || refreshToken is null)
        {
            return null;
        }

        cookieStore.Write(httpContext, accessToken, refreshToken);
        json["accessToken"] = SessionClaimsToken.Create(accessToken) ?? string.Empty;
        json["refreshToken"] = string.Empty;
        return JsonSerializer.SerializeToUtf8Bytes(json);
    }

    private static string? ReadString(JsonObject? json, string name) =>
        json?[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    /// <summary>
    /// Keeps the HttpOnly session cookies on this origin: the upstream gets the bearer, never the cookie
    /// jar's copies of the tokens. Every other cookie passes through.
    /// </summary>
    private static void RemoveSessionCookies(HttpRequestHeaders headers)
    {
        if (!headers.TryGetValues(HeaderNames.Cookie, out var values))
        {
            return;
        }

        var kept = values
            .SelectMany(static value => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(static pair => !SessionCookieNames.Any(name =>
                pair.StartsWith(name + "=", StringComparison.Ordinal)))
            .ToList();

        headers.Remove(HeaderNames.Cookie);
        if (kept.Count > 0)
        {
            headers.TryAddWithoutValidation(HeaderNames.Cookie, string.Join("; ", kept));
        }
    }
}

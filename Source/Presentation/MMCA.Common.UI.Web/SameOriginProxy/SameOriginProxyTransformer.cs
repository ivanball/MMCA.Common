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
}

/// <summary>
/// One request's YARP transform. Starts from <see cref="HttpTransformer.Default"/> (X-Forwarded-*
/// headers, upstream Host) and then: maps <c>{prefix}/rest</c> to <c>{gateway}/rest</c>, replaces
/// whatever <c>Authorization</c> the browser sent with the session's bearer (or none), keeps the session
/// cookies and the CSRF header off the upstream request, and applies the <see cref="ProxyResponseMode"/>.
/// When <c>captureUnauthorized</c> is set, an upstream 401 is swallowed (nothing copied) so the caller
/// can refresh and replay the request once. Not shared: it carries per-request state.
/// </summary>
internal sealed class SameOriginProxyTransformer(
    PathString pathPrefix,
    string? bearerToken,
    ProxyResponseMode mode,
    bool captureUnauthorized,
    ISessionCookieStore cookieStore) : HttpTransformer
{
    private static readonly string[] SessionCookieNames =
        [SessionCookieEndpoints.AccessTokenCookieName, SessionCookieEndpoints.RefreshTokenCookieName];

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

        if (mode == ProxyResponseMode.TokenIssuing)
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

        return copyBody;
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

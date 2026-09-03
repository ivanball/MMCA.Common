using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Shared.Concurrency;

namespace MMCA.Common.API.SessionCookies;

/// <summary>A valid access token plus its UTC expiry, acquired from the session cookies.</summary>
public readonly record struct SessionTokenResult(string AccessToken, DateTime AccessTokenExpiry);

/// <summary>
/// JSON body returned by <c>POST /auth/session/token</c> — the access token only. The refresh token
/// is never serialized to the browser; it lives only in the HttpOnly cookie.
/// </summary>
public sealed record SessionTokenResponse(string AccessToken, DateTime AccessTokenExpiry);

/// <summary>
/// Server-side "validate-or-refresh" over the HttpOnly session cookies. If the access cookie's JWT is
/// still valid it is returned as-is; otherwise the refresh cookie is exchanged at the API's
/// <c>auth/refresh</c> endpoint server-to-server (so the refresh token never reaches browser JS), the
/// rotated tokens are written back as HttpOnly cookies, and the fresh access token is stashed on
/// <see cref="HttpContext.Items"/> so the current request's SSR authentication can read it.
/// </summary>
public interface ICookieSessionRefresher
{
    /// <summary>
    /// Returns a currently-valid access token for the request's session, refreshing from the refresh
    /// cookie when the access cookie is expired (setting fresh cookies as a side effect), or
    /// <see langword="null"/> when there is no valid session.
    /// </summary>
    Task<SessionTokenResult?> GetOrRefreshAsync(HttpContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Singleton refresher. A per-token lock plus a short rotation-grace cache collapse concurrent
/// refreshes (single-flight): the first request rotates and caches the result keyed by the OLD refresh
/// token; queued/slightly-late siblings carrying the same expired pair return the cached result instead
/// of rotating again — preventing double rotation under a thundering herd.
/// <para>
/// The lock is striped by refresh token rather than process-wide: it is held across an outbound HTTP
/// call, so a single semaphore serialized every unrelated user's cold navigation behind whichever
/// refresh was in flight. Two unrelated tokens can still share a stripe, which is harmless because the
/// rotation-grace cache is re-checked per token after acquiring.
/// </para>
/// </summary>
internal sealed partial class CookieSessionRefresher(
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    IWebHostEnvironment environment,
    ILogger<CookieSessionRefresher> logger) : ICookieSessionRefresher
{
    internal const string RefreshClientName = "SessionCookieRefreshClient";

    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RotationGrace = TimeSpan.FromSeconds(10);

    private readonly KeyedSemaphoreStripe _refreshLocks = new();

    public async Task<SessionTokenResult?> GetOrRefreshAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var accessToken = context.Request.Cookies[SessionCookieEndpoints.AccessTokenCookieName];
        if (TryReadValidExpiry(accessToken, out var expiry))
        {
            return new SessionTokenResult(accessToken!, expiry);
        }

        var refreshToken = context.Request.Cookies[SessionCookieEndpoints.RefreshTokenCookieName];
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return null;
        }

        var refreshed = await RefreshAsync(accessToken ?? string.Empty, refreshToken, cancellationToken).ConfigureAwait(false);
        if (refreshed is null || string.IsNullOrWhiteSpace(refreshed.Value.AccessToken))
        {
            return null;
        }

        var auth = refreshed.Value;
        SessionCookieJar.Append(context, auth.AccessToken, auth.RefreshToken, environment);

        // Make the freshly-minted access token visible to this request's SSR authentication, which reads
        // via CookieTokenReader (the Set-Cookie above only affects subsequent requests).
        context.Items[CookieTokenReader.FreshAccessTokenItemKey] = auth.AccessToken;
        return new SessionTokenResult(auth.AccessToken, auth.AccessTokenExpiry);
    }

    private async Task<AuthenticationResponse?> RefreshAsync(string accessToken, string refreshToken, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(CacheKey(refreshToken), out AuthenticationResponse cached))
        {
            return cached;
        }

        using var releaser = await _refreshLocks.AcquireAsync(CacheKey(refreshToken), cancellationToken).ConfigureAwait(false);

        // Double-check: a request we were queued behind may have just rotated this same token.
        if (cache.TryGetValue(CacheKey(refreshToken), out cached))
        {
            return cached;
        }

        return await CallRefreshAsync(accessToken, refreshToken).ConfigureAwait(false);
    }

    private async Task<AuthenticationResponse?> CallRefreshAsync(string accessToken, string refreshToken)
    {
        var client = httpClientFactory.CreateClient(RefreshClientName);

        // A transport failure or a malformed body means "no session right now", not a broken request:
        // this runs during SSR, so an escaping exception turned a signed-in user's navigation into a
        // 500 instead of an anonymous render. The failure is deliberately NOT cached (only a
        // successful rotation reaches cache.Set below), so the next navigation retries. A missing
        // BaseAddress raises InvalidOperationException and is left to propagate: that is a host
        // configuration error, not a runtime condition.
        try
        {
            // CancellationToken.None: once we hold the lock the refresh must complete (and write its cookies)
            // regardless of whether the triggering request was aborted; the call is short.
            using var response = await client.PostAsJsonAsync(
                new Uri("auth/refresh", UriKind.Relative),
                new RefreshTokenRequest(accessToken, refreshToken),
                CancellationToken.None).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var auth = await response.Content.ReadFromJsonAsync<AuthenticationResponse>(CancellationToken.None).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(auth.AccessToken))
            {
                return null;
            }

            // Cache by the OLD refresh token so a slightly-late sibling request gets the same rotated pair.
            cache.Set(CacheKey(refreshToken), auth, RotationGrace);
            return auth;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException)
        {
            LogRefreshCallFailed(logger, ex);
            return null;
        }
    }

    private static bool TryReadValidExpiry(string? token, out DateTime expiry)
    {
        expiry = default;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var handler = new JwtSecurityTokenHandler();
        if (!handler.CanReadToken(token))
        {
            return false;
        }

        try
        {
            var jwt = handler.ReadJwtToken(token);
            if (jwt.ValidTo <= DateTime.UtcNow + ClockSkew)
            {
                return false;
            }

            expiry = jwt.ValidTo;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// The rotation-grace cache key, which is also the striping key. Internal rather than private so a
    /// concurrency test can pick two refresh tokens that do not land on the same stripe.
    /// </summary>
    internal static string CacheKey(string refreshToken) => $"mmca:session-refresh:{refreshToken}";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session cookie refresh call failed; the request renders anonymously and the next navigation retries")]
    private static partial void LogRefreshCallFailed(ILogger logger, Exception exception);
}

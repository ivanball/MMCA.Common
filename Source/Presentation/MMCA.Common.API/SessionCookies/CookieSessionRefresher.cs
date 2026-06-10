using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using MMCA.Common.Shared.Auth;

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
/// Singleton refresher. A process-wide lock plus a short rotation-grace cache collapse concurrent
/// refreshes (single-flight): the first request rotates and caches the result keyed by the OLD refresh
/// token; queued/slightly-late siblings carrying the same expired pair return the cached result instead
/// of rotating again — preventing double rotation under a thundering herd. Refreshes are infrequent
/// (only on access-token expiry during a cold navigation) and each holds the lock for one short HTTP call.
/// </summary>
internal sealed class CookieSessionRefresher(
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    IWebHostEnvironment environment) : ICookieSessionRefresher, IDisposable
{
    internal const string RefreshClientName = "SessionCookieRefreshClient";

    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RotationGrace = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

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

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check: a request we were queued behind may have just rotated this same token.
            if (cache.TryGetValue(CacheKey(refreshToken), out cached))
            {
                return cached;
            }

            return await CallRefreshAsync(accessToken, refreshToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<AuthenticationResponse?> CallRefreshAsync(string accessToken, string refreshToken)
    {
        var client = httpClientFactory.CreateClient(RefreshClientName);

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

    private static string CacheKey(string refreshToken) => $"mmca:session-refresh:{refreshToken}";

    public void Dispose() => _refreshLock.Dispose();
}

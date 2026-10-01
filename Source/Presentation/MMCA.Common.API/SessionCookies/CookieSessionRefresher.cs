using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

    /// <summary>
    /// <see cref="GetOrRefreshAsync"/> with the failure kept apart:
    /// <see cref="SessionRefreshStatus.Rejected"/> when there is no refresh cookie or the identity
    /// endpoint refused it (the session is over), <see cref="SessionRefreshStatus.Unavailable"/> when
    /// the refresh could not be decided right now (5xx, 429, timeout, network), so a caller that
    /// clears cookies on failure does so only for a session that is really dead.
    /// </summary>
    Task<SessionRefreshOutcome> ValidateOrRefreshAsync(HttpContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Exchanges the refresh cookie for a new token pair even when the access cookie still looks valid
    /// (the API rejected it: revoked, or signed with a rotated key), writing the rotated cookies as a
    /// side effect. Single-flighted exactly like <see cref="GetOrRefreshAsync"/>: concurrent callers
    /// holding the same refresh cookie share one rotation. The outcome is
    /// <see cref="SessionRefreshStatus.Rejected"/> when there is no refresh cookie or the identity
    /// endpoint refused it, and <see cref="SessionRefreshStatus.Unavailable"/> when the exchange failed
    /// for a transient reason.
    /// </summary>
    Task<SessionRefreshOutcome> RefreshAsync(HttpContext context, CancellationToken cancellationToken = default);
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
    ILogger<CookieSessionRefresher> logger,
    TimeProvider timeProvider,
    IOptions<SessionCookieSettings> cookieSettings) : ICookieSessionRefresher
{
    internal const string RefreshClientName = "SessionCookieRefreshClient";

    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RotationGrace = TimeSpan.FromSeconds(10);

    private readonly KeyedSemaphoreStripe _refreshLocks = new();

    public async Task<SessionTokenResult?> GetOrRefreshAsync(HttpContext context, CancellationToken cancellationToken = default) =>
        (await ValidateOrRefreshAsync(context, cancellationToken).ConfigureAwait(false)).Session;

    public async Task<SessionRefreshOutcome> ValidateOrRefreshAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var accessToken = context.Request.Cookies[SessionCookieEndpoints.AccessTokenCookieName];
        if (TryReadValidExpiry(accessToken, out var expiry))
        {
            return SessionRefreshOutcome.Refreshed(new SessionTokenResult(accessToken!, expiry));
        }

        return await RefreshFromCookiesAsync(context, accessToken, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionRefreshOutcome> RefreshAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var accessToken = context.Request.Cookies[SessionCookieEndpoints.AccessTokenCookieName];
        return await RefreshFromCookiesAsync(context, accessToken, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Maps a non-success status from <c>auth/refresh</c>: 400, 401 and 403 are the identity endpoint
    /// refusing the refresh token, so the session is over. Anything else (5xx, 429, 408, a 404 from a
    /// misrouted gateway) says nothing about the token, so the session is kept for a later retry.
    /// </summary>
    internal static SessionRefreshStatus ClassifyFailure(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? SessionRefreshStatus.Rejected
            : SessionRefreshStatus.Unavailable;

    private async Task<SessionRefreshOutcome> RefreshFromCookiesAsync(HttpContext context, string? accessToken, CancellationToken cancellationToken)
    {
        var refreshToken = context.Request.Cookies[SessionCookieEndpoints.RefreshTokenCookieName];
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return SessionRefreshOutcome.Rejected();
        }

        var (auth, failure) = await RefreshAsync(accessToken ?? string.Empty, refreshToken, cancellationToken).ConfigureAwait(false);
        if (auth is null)
        {
            return failure!;
        }

        SessionCookieJar.Append(context, auth.Value.AccessToken, auth.Value.RefreshToken, environment, cookieSettings.Value.SameSite);

        // Make the freshly-minted access token visible to this request's SSR authentication, which reads
        // via CookieTokenReader (the Set-Cookie above only affects subsequent requests).
        context.Items[CookieTokenReader.FreshAccessTokenItemKey] = auth.Value.AccessToken;
        return SessionRefreshOutcome.Refreshed(new SessionTokenResult(auth.Value.AccessToken, auth.Value.AccessTokenExpiry));
    }

    private async Task<(AuthenticationResponse? Auth, SessionRefreshOutcome? Failure)> RefreshAsync(
        string accessToken, string refreshToken, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(CacheKey(refreshToken), out AuthenticationResponse cached))
        {
            return (cached, null);
        }

        using var releaser = await _refreshLocks.AcquireAsync(CacheKey(refreshToken), cancellationToken).ConfigureAwait(false);

        // Double-check: a request we were queued behind may have just rotated this same token.
        if (cache.TryGetValue(CacheKey(refreshToken), out cached))
        {
            return (cached, null);
        }

        return await CallRefreshAsync(accessToken, refreshToken).ConfigureAwait(false);
    }

    private async Task<(AuthenticationResponse? Auth, SessionRefreshOutcome? Failure)> CallRefreshAsync(string accessToken, string refreshToken)
    {
        var client = httpClientFactory.CreateClient(RefreshClientName);

        // A transport failure or a malformed body means "no session right now", not a broken request:
        // this runs during SSR, so an escaping exception turned a signed-in user's navigation into a
        // 500 instead of an anonymous render. It is Unavailable, not Rejected: nothing said the
        // refresh token is dead, so a caller that clears cookies must keep them. The failure is
        // deliberately NOT cached (only a successful rotation reaches cache.Set below), so the next
        // navigation retries. A missing BaseAddress raises InvalidOperationException and is left to
        // propagate: that is a host configuration error, not a runtime condition.
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
                return ClassifyFailure(response.StatusCode) == SessionRefreshStatus.Rejected
                    ? (null, SessionRefreshOutcome.Rejected())
                    : (null, SessionRefreshOutcome.Unavailable(ReadRetryAfter(response)));
            }

            var auth = await response.Content.ReadFromJsonAsync<AuthenticationResponse>(CancellationToken.None).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(auth.AccessToken))
            {
                return (null, SessionRefreshOutcome.Unavailable());
            }

            // Cache by the OLD refresh token so a slightly-late sibling request gets the same rotated pair.
            cache.Set(CacheKey(refreshToken), auth, RotationGrace);
            return (auth, null);
        }
        // Polly.ExecutionRejectedException covers the resilience pipeline's own refusals (a timed-out
        // attempt, an open circuit, a rate-limited call): with the gateway down the standard handler
        // throws TimeoutRejectedException, which is neither of the transport exceptions above.
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException
            or Polly.ExecutionRejectedException)
        {
            LogRefreshCallFailed(logger, ex);
            return (null, SessionRefreshOutcome.Unavailable());
        }
    }

    private TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not { } retryAfter)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (retryAfter.Date is { } date)
        {
            var remaining = date - timeProvider.GetUtcNow();
            return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }

        return null;
    }

    private bool TryReadValidExpiry(string? token, out DateTime expiry)
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
            if (jwt.ValidTo <= timeProvider.GetUtcNow().UtcDateTime + ClockSkew)
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

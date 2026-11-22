using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using MMCA.Common.UI.Services.Api;
using MMCA.Common.UI.Services.Auth.Tokens;

namespace MMCA.Common.UI.Services.Auth;

/// <summary>
/// HTTP message handler that attaches the stored JWT Bearer token to every outgoing API request.
/// Registered in the <c>"APIClient"</c> HttpClient pipeline via <c>AddHttpMessageHandler</c>.
/// A request that sets <see cref="SkipBearer"/> to <see langword="true"/> passes through untouched,
/// without reading the token storage at all, and so does a request that already carries an
/// <c>Authorization</c> header: the forced-refresh replay sets the token it just acquired, and
/// replacing it with the stored one would resend the token the server just rejected.
/// <para>
/// <b>A 401 to a request that carried a bearer is reported to the storage that holds the token</b>
/// (<see cref="ISessionAwareTokenStorage.RenewRejectedAccessTokenAsync"/>), which makes one refresh
/// attempt: a refused refresh ends the session (the stored tokens are cleared and the auth state signs
/// out, so a protected page lands on the sign-in page), an acquired token serves the next call. The
/// response itself is passed through unchanged and never replayed. A token acquired that way is
/// remembered, and a 401 to it is NOT reported again, so an API that keeps refusing a freshly issued
/// token cannot drive a refresh per request.
/// </para>
/// <para>
/// The handler resolves its own storage in the <c>IHttpClientFactory</c> handler scope, which in a
/// browser host is NOT the instance the component holds (see
/// <see cref="AuthenticatedServiceBase"/>), so the bearer a service attaches from its own storage is
/// registered with <see cref="TrackTokenOwner"/> and its 401 goes to that owner. A token with no
/// registered owner goes to this handler's storage, which changes nothing unless it holds that token.
/// </para>
/// </summary>
public sealed class AuthDelegatingHandler(
    ITokenStorageService tokenStorageService) : DelegatingHandler
{
    /// <summary>
    /// Request option that opts one request out of the bearer header. The token refresh POST sets it:
    /// that endpoint is anonymous, and reading the token storage from inside a refresh would re-enter
    /// the very acquisition that is waiting for this request to complete.
    /// </summary>
    public static readonly HttpRequestOptionsKey<bool> SkipBearer = new("MMCA.Common.UI.Auth.SkipBearer");

    /// <summary>Entries kept before a sweep drops the ones whose owner is gone or that outlived any token.</summary>
    private const int SweepThreshold = 1024;

    /// <summary>How long a remembered token is kept, in milliseconds: 24 hours, longer than any access token lives.</summary>
    private const long RetentionMs = 24L * 60 * 60 * 1000;

    /// <summary>Which storage attached each bearer, so its 401 reaches the instance that holds it.</summary>
    private static readonly ConcurrentDictionary<string, TokenOwner> Owners = new(StringComparer.Ordinal);

    /// <summary>Tokens acquired by a 401 renewal: a 401 to one of them is not renewed again.</summary>
    private static readonly ConcurrentDictionary<string, long> RenewedTokens = new(StringComparer.Ordinal);

    /// <summary>
    /// Records that <paramref name="owner"/> handed out <paramref name="accessToken"/> for a request,
    /// so a 401 to it is reported to that storage instance rather than to the handler's own. Held
    /// weakly: a circuit or scope that ends is not kept alive by this map.
    /// </summary>
    /// <param name="accessToken">The bearer attached to the request.</param>
    /// <param name="owner">The storage that holds it.</param>
    internal static void TrackTokenOwner(string accessToken, ITokenStorageService owner)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || owner is not ISessionAwareTokenStorage sessionAware)
        {
            return;
        }

        TrackTokenOwnerAt(accessToken, new WeakReference<ISessionAwareTokenStorage>(sessionAware), Environment.TickCount64);
    }

    /// <summary>
    /// Stores an owner entry as given, so a test can age it or hand it a reference whose storage is gone.
    /// </summary>
    /// <param name="accessToken">The bearer attached to the request.</param>
    /// <param name="owner">The weak reference to the storage that holds it.</param>
    /// <param name="trackedAt">The <see cref="Environment.TickCount64"/> the entry is stamped with.</param>
    internal static void TrackTokenOwnerAt(string accessToken, WeakReference<ISessionAwareTokenStorage> owner, long trackedAt)
    {
        Owners[accessToken] = new TokenOwner(owner, trackedAt);
        SweepIfLarge();
    }

    /// <summary>Remembers <paramref name="accessToken"/> as the product of a 401 renewal, stamped <paramref name="renewedAt"/>.</summary>
    /// <param name="accessToken">The token a renewal produced.</param>
    /// <param name="renewedAt">The <see cref="Environment.TickCount64"/> the entry is stamped with.</param>
    internal static void MarkRenewedAt(string accessToken, long renewedAt)
    {
        RenewedTokens[accessToken] = renewedAt;
        SweepIfLarge();
    }

    /// <summary>Whether a 401 to <paramref name="accessToken"/> would still reach a tracked, live owner.</summary>
    /// <param name="accessToken">The bearer.</param>
    /// <returns><see langword="true"/> when the token has an entry whose owner is still alive.</returns>
    internal static bool HasLiveOwner(string accessToken) =>
        Owners.TryGetValue(accessToken, out var owner) && owner.Storage.TryGetTarget(out _);

    /// <summary>Whether <paramref name="accessToken"/> has any owner entry, live or dead.</summary>
    /// <param name="accessToken">The bearer.</param>
    /// <returns><see langword="true"/> when the map still holds an entry for the token.</returns>
    internal static bool IsTrackedOwner(string accessToken) => Owners.ContainsKey(accessToken);

    /// <summary>Whether <paramref name="accessToken"/> is remembered as the product of a renewal.</summary>
    /// <param name="accessToken">The token.</param>
    /// <returns><see langword="true"/> when a 401 to it would not be renewed again.</returns>
    internal static bool IsMarkedRenewed(string accessToken) => RenewedTokens.ContainsKey(accessToken);

    /// <summary>
    /// Drops, once a map holds more than <see cref="SweepThreshold"/> entries, the owner entries whose
    /// storage is gone or that outlived <see cref="RetentionMs"/>, and the renewal entries that outlived it.
    /// </summary>
    internal static void SweepIfLarge()
    {
        var now = Environment.TickCount64;
        SweepOwners(now);
        SweepRenewed(now);
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SendWithBearerAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            // The shared retry policy sees only this exception, not the request, so it is told here
            // whether the request may be resent (L185): a keyless POST or PATCH may not, once sent.
            AuthenticatedServiceBase.MarkReplaySafety(ex, request);
            throw;
        }
    }

    private static string? BearerOf(HttpRequestMessage request) =>
        request.Headers.Authorization is { Parameter: { } token } header
            && string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(token)
                ? token
                : null;

    private static void SweepOwners(long now)
    {
        if (Owners.Count <= SweepThreshold)
        {
            return;
        }

        // A ConcurrentDictionary may be modified while it is enumerated.
        foreach (var entry in Owners.Where(entry => IsStale(entry.Value, now)))
        {
            Owners.TryRemove(entry.Key, out _);
        }
    }

    private static void SweepRenewed(long now)
    {
        if (RenewedTokens.Count <= SweepThreshold)
        {
            return;
        }

        foreach (var entry in RenewedTokens.Where(entry => IsExpired(entry.Value, now)))
        {
            RenewedTokens.TryRemove(entry.Key, out _);
        }
    }

    private static bool IsStale(TokenOwner owner, long now) =>
        IsExpired(owner.TrackedAt, now) || !owner.Storage.TryGetTarget(out _);

    private static bool IsExpired(long stampedAt, long now) => now - stampedAt > RetentionMs;

    private async Task<HttpResponseMessage> SendWithBearerAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Options.TryGetValue(SkipBearer, out var skip) && skip)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        if (request.Headers.Authorization is null)
        {
            var token = await tokenStorageService.GetAccessTokenAsync();
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }

        // Read before sending: the same-origin proxy handler further in strips the header.
        var sentToken = BearerOf(request);
        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized && sentToken is not null)
        {
            await ReportRejectedAsync(sentToken, tokenStorageService);
        }

        return response;
    }

    /// <summary>
    /// Hands a refused bearer to the storage that holds it for one refresh attempt. Best effort: the
    /// 401 reaches the caller whatever happens here, so a failure is swallowed rather than replacing
    /// the server's answer with an exception. Also the 401 handling of a bearer attached outside this
    /// pipeline (the notification hub's WebSocket handshake), so every refusal of the session's bearer
    /// gets the same single renewal and the same session end (Store Run 5 R6a).
    /// </summary>
    /// <param name="rejectedToken">The bearer the server refused.</param>
    /// <param name="fallbackOwner">
    /// The storage to report to when no live owner was registered for the token (this handler's own,
    /// or the storage the caller attached the bearer from).
    /// </param>
    /// <returns>A task that completes when the renewal attempt has finished.</returns>
    internal static async Task ReportRejectedAsync(string rejectedToken, ITokenStorageService fallbackOwner)
    {
        if (RenewedTokens.ContainsKey(rejectedToken))
        {
            // Already the product of a renewal: refreshing again would loop.
            return;
        }

        var owner = Owners.TryGetValue(rejectedToken, out var tracked) && tracked.Storage.TryGetTarget(out var target)
            ? target
            : fallbackOwner as ISessionAwareTokenStorage;
        if (owner is null)
        {
            return;
        }

        try
        {
            var renewed = await owner.RenewRejectedAccessTokenAsync(rejectedToken);
            if (!string.IsNullOrWhiteSpace(renewed) && !string.Equals(renewed, rejectedToken, StringComparison.Ordinal))
            {
                TrackTokenOwner(renewed, owner);
                MarkRenewedAt(renewed, Environment.TickCount64);
            }
        }
#pragma warning disable CA1031 // Best effort by design: the caller still receives the server's 401, which is the answer that matters.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            // Nothing to do: a storage or refresher fault leaves the session exactly as it was.
        }
    }

    /// <summary>The storage that attached a bearer, and when.</summary>
    private sealed record TokenOwner(WeakReference<ISessionAwareTokenStorage> Storage, long TrackedAt);
}

using System.Net;
using System.Net.Http.Json;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.UI.Services.Auth.Tokens;

/// <summary>
/// MAUI token refresher. Exchanges the refresh token held in OS SecureStorage directly against the API's
/// cross-origin <c>auth/refresh</c> endpoint and persists the rotated pair back to SecureStorage. Used on
/// the MAUI host, which has no browser/DOM (and thus no XSS surface) so direct token handling is acceptable.
/// <para>
/// It depends on <see cref="ISecureTokenStore"/> rather than <see cref="ITokenStorageService"/> on
/// purpose: every operation it performs is a raw read or write, and taking the raw store keeps the
/// dependency graph acyclic (the freshness-checking storage depends on this refresher, which depends
/// on the raw store). Taking the storage service instead would close that loop and let a refresh
/// re-enter the very acquisition that started it.
/// </para>
/// <para>
/// The same cycle also exists indirectly through the HTTP pipeline: the <c>APIClient</c> carries
/// <see cref="AuthDelegatingHandler"/>, which reads the storage service for a bearer. The refresh POST
/// therefore sets <see cref="AuthDelegatingHandler.SkipBearer"/> (the endpoint is anonymous), so it never
/// reaches the storage instance that is awaiting it.
/// </para>
/// <para>
/// A <c>409 Conflict</c> (<c>Auth.RefreshSuperseded</c>) is transient, never "no session": the
/// presented token was rotated by another request inside the server's reuse grace. If that request
/// was this process (another refresh that already stored its pair), the stored pair is the answer.
/// Otherwise the stored pair is kept untouched, so the next attempt presents the same token again;
/// if someone else rotated it, that later presentation falls outside the grace and the server's reuse
/// detection (BR-206) revokes the whole family, including the other holder's session. Clearing the
/// pair here would let a stolen-and-rotated token live for the full refresh lifetime.
/// </para>
/// </summary>
public sealed class DirectApiTokenRefresher(
    IHttpClientFactory httpClientFactory,
    ISecureTokenStore tokenStore) : ISessionAwareTokenRefresher
{
    private const string ApiClientName = "APIClient";

    /// <inheritdoc />
    public async Task<string?> AcquireAccessTokenAsync(CancellationToken cancellationToken = default) =>
        (await TryAcquireAccessTokenAsync(cancellationToken)).AccessToken;

    /// <inheritdoc />
    public async Task<TokenAcquisition> TryAcquireAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var accessToken = await tokenStore.GetAccessTokenAsync();
        var refreshToken = await tokenStore.GetRefreshTokenAsync();

        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken))
        {
            return TokenAcquisition.NoSession;
        }

        using var httpClient = httpClientFactory.CreateClient(ApiClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("auth/refresh", UriKind.Relative))
        {
            Content = JsonContent.Create(new RefreshTokenRequest(accessToken, refreshToken)),
        };
        request.Options.Set(AuthDelegatingHandler.SkipBearer, true);
        var response = await httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return await ReadSupersededOutcomeAsync(refreshToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            return TokenAcquisition.NoSession;
        }

        var result = await response.Content.ReadFromJsonAsync<AuthenticationResponse>(cancellationToken);
        if (string.IsNullOrWhiteSpace(result.AccessToken))
        {
            return TokenAcquisition.NoSession;
        }

        await tokenStore.SetTokensAsync(result.AccessToken, result.RefreshToken);
        return TokenAcquisition.Acquired(result.AccessToken);
    }

    /// <summary>
    /// The answer to a 409: the newer pair when another refresh in this process already stored one,
    /// otherwise a transient failure that leaves the stored pair exactly as it was.
    /// </summary>
    private async Task<TokenAcquisition> ReadSupersededOutcomeAsync(string presentedRefreshToken)
    {
        var storedAccessToken = await tokenStore.GetAccessTokenAsync();
        var storedRefreshToken = await tokenStore.GetRefreshTokenAsync();

        return !string.IsNullOrWhiteSpace(storedAccessToken)
            && !string.IsNullOrWhiteSpace(storedRefreshToken)
            && !string.Equals(storedRefreshToken, presentedRefreshToken, StringComparison.Ordinal)
                ? TokenAcquisition.Acquired(storedAccessToken)
                : TokenAcquisition.Unavailable;
    }
}

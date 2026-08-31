using System.Net.Http.Json;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.UI.Services.Auth;

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
/// </summary>
public sealed class DirectApiTokenRefresher(
    IHttpClientFactory httpClientFactory,
    ISecureTokenStore tokenStore) : ITokenRefresher
{
    private const string ApiClientName = "APIClient";

    public async Task<string?> AcquireAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var accessToken = await tokenStore.GetAccessTokenAsync();
        var refreshToken = await tokenStore.GetRefreshTokenAsync();

        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken))
        {
            return null;
        }

        using var httpClient = httpClientFactory.CreateClient(ApiClientName);
        var request = new RefreshTokenRequest(accessToken, refreshToken);
        var response = await httpClient.PostAsJsonAsync(new Uri("auth/refresh", UriKind.Relative), request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var result = await response.Content.ReadFromJsonAsync<AuthenticationResponse>(cancellationToken);
        if (string.IsNullOrWhiteSpace(result.AccessToken))
        {
            return null;
        }

        await tokenStore.SetTokensAsync(result.AccessToken, result.RefreshToken);
        return result.AccessToken;
    }
}

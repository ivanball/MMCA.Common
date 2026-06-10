using System.Net.Http.Json;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.UI.Services.Auth;

/// <summary>
/// MAUI token refresher. Exchanges the refresh token held in OS SecureStorage directly against the API's
/// cross-origin <c>auth/refresh</c> endpoint and persists the rotated pair back to SecureStorage. Used on
/// the MAUI host, which has no browser/DOM (and thus no XSS surface) so direct token handling is acceptable.
/// </summary>
public sealed class DirectApiTokenRefresher(
    IHttpClientFactory httpClientFactory,
    ITokenStorageService tokenStorageService) : ITokenRefresher
{
    private const string ApiClientName = "APIClient";

    public async Task<string?> AcquireAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var accessToken = await tokenStorageService.GetAccessTokenAsync();
        var refreshToken = await tokenStorageService.GetRefreshTokenAsync();

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

        await tokenStorageService.SetTokensAsync(result.AccessToken, result.RefreshToken);
        return result.AccessToken;
    }
}

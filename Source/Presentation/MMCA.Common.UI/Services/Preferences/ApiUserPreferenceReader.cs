using System.Net.Http.Json;
using MMCA.Common.UI.Services.Auth.Tokens;

namespace MMCA.Common.UI.Services.Preferences;

/// <summary>
/// Default <see cref="IUserPreferenceReader"/>: GETs <c>auth/preferences</c> via the shared
/// <c>"APIClient"</c> (bearer token attached by the auth handler). Returns empty preferences for
/// anonymous users or on any transport error, so login reconciliation is strictly best-effort
/// (ADR-027 / ADR-028).
/// </summary>
/// <param name="httpClientFactory">Factory for the named <c>"APIClient"</c>.</param>
/// <param name="tokenStorageService">Used to detect whether a user is signed in.</param>
public sealed class ApiUserPreferenceReader(
    IHttpClientFactory httpClientFactory,
    ITokenStorageService tokenStorageService) : IUserPreferenceReader
{
    private static readonly UserPreferences Empty = new(null, null);

    /// <summary>Matches the token-storage skew, so this agrees with the layer that does the refreshing.</summary>
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(30);

    /// <inheritdoc/>
    public async Task<UserPreferences> GetAsync(CancellationToken cancellationToken = default)
    {
        var token = await tokenStorageService.GetAccessTokenAsync();

        // Same guard as the writer: an expired or unreadable token buys a guaranteed 401, and this read
        // is best-effort, so the caller cannot act on the failure either way. IsFresh also covers the
        // anonymous (null) case.
        if (!JwtTokenInfo.IsFresh(token, ExpirySkew))
        {
            return Empty;
        }

        try
        {
            var client = httpClientFactory.CreateClient("APIClient");
            var preferences = await client.GetFromJsonAsync<UserPreferences>(
                new Uri("auth/preferences", UriKind.Relative),
                cancellationToken);
            return preferences ?? Empty;
        }
        catch (HttpRequestException)
        {
            return Empty;
        }
        catch (TaskCanceledException)
        {
            return Empty;
        }
    }
}

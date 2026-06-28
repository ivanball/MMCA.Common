using System.Net.Http.Json;
using MMCA.Common.UI.Services.Auth;

namespace MMCA.Common.UI.Services;

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

    /// <inheritdoc/>
    public async Task<UserPreferences> GetAsync(CancellationToken cancellationToken = default)
    {
        var token = await tokenStorageService.GetAccessTokenAsync();
        if (string.IsNullOrWhiteSpace(token))
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

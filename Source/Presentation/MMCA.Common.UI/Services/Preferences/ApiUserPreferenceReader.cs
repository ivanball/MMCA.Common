using System.Net.Http.Headers;
using System.Net.Http.Json;
using MMCA.Common.UI.Services.Auth.Tokens;

namespace MMCA.Common.UI.Services.Preferences;

/// <summary>
/// Default <see cref="IUserPreferenceReader"/>: GETs <c>auth/preferences</c> via the shared
/// <c>"APIClient"</c>, attaching the bearer token it read itself on the request message. Returns empty
/// preferences for anonymous users or on any transport error, so login reconciliation is strictly
/// best-effort (ADR-027 / ADR-028).
/// <para>
/// The bearer goes on the request rather than being left to <c>AuthDelegatingHandler</c>: in Blazor
/// Server that handler resolves in a separate DI scope whose token store is empty, so relying on it
/// sends the read anonymous (the same scope problem <c>AuthenticatedServiceBase</c> documents). The
/// handler leaves an existing <c>Authorization</c> header alone, so WASM and MAUI see no change. The
/// header is set per request, never on the factory client's shared default headers.
/// </para>
/// </summary>
/// <param name="httpClientFactory">Factory for the named <c>"APIClient"</c>.</param>
/// <param name="tokenStorageService">Supplies the token this reader decides against and attaches.</param>
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
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("auth/preferences", UriKind.Relative));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Empty;
            }

            var preferences = await response.Content.ReadFromJsonAsync<UserPreferences>(cancellationToken);
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

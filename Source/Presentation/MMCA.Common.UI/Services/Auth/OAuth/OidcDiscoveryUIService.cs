using System.Net;
using MMCA.Common.Shared.Auth.ExternalProviders;

namespace MMCA.Common.UI.Services.Auth.OAuth;

/// <summary>
/// <see cref="IOidcDiscoveryUIService"/> over the API's anonymous <c>GET auth/oauth/discover</c>
/// endpoint, through the named <c>"APIClient"</c> HttpClient.
/// </summary>
/// <param name="httpClientFactory">Factory for the named <c>"APIClient"</c> HttpClient.</param>
internal sealed class OidcDiscoveryUIService(IHttpClientFactory httpClientFactory) : IOidcDiscoveryUIService
{
    private const string ApiClientName = "APIClient";

    /// <inheritdoc />
    /// <exception cref="HttpRequestException">The API answered with a failure other than 404.</exception>
    public async Task<OidcDiscoveryResponse?> DiscoverAsync(string email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);

        using var httpClient = httpClientFactory.CreateClient(ApiClientName);
        using var response = await httpClient.GetAsync(
            new Uri($"auth/oauth/discover?email={Uri.EscapeDataString(email)}", UriKind.Relative),
            cancellationToken);

        // 404 is the answer "no provider claims this domain"; any other failure (throttled, outage)
        // must not read as that answer.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<OidcDiscoveryResponse>(cancellationToken);
    }
}

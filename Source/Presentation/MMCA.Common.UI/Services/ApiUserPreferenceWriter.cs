using System.Net.Http.Json;
using MMCA.Common.UI.Services.Auth;

namespace MMCA.Common.UI.Services;

/// <summary>
/// Default <see cref="IUserPreferenceWriter"/>: PUTs the preference to <c>auth/preferences</c> via the
/// shared <c>"APIClient"</c> (which already attaches the bearer token + Accept-Language). No-ops for
/// anonymous users and swallows transport errors, so persistence is strictly best-effort over the
/// cookie-based runtime channel (ADR-027 / ADR-028). Hosts without that endpoint (e.g. the Helpdesk seed)
/// simply do not register this writer.
/// </summary>
/// <param name="httpClientFactory">Factory for the named <c>"APIClient"</c>.</param>
/// <param name="tokenStorageService">Used to detect whether a user is signed in.</param>
public sealed class ApiUserPreferenceWriter(
    IHttpClientFactory httpClientFactory,
    ITokenStorageService tokenStorageService) : IUserPreferenceWriter
{
    private sealed record UserPreferencesRequest(string? Culture, string? Theme);

    /// <inheritdoc/>
    public async Task SaveAsync(string? culture, string? theme, CancellationToken cancellationToken = default)
    {
        // Anonymous users have no profile to persist to — the cookie is their only channel.
        var token = await tokenStorageService.GetAccessTokenAsync();
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        try
        {
            var client = httpClientFactory.CreateClient("APIClient");
            using var response = await client.PutAsJsonAsync(
                new Uri("auth/preferences", UriKind.Relative),
                new UserPreferencesRequest(culture, theme),
                cancellationToken);
        }
        catch (HttpRequestException)
        {
            // Best-effort: the cookie/localStorage already hold the choice for this device.
        }
        catch (TaskCanceledException)
        {
            // Navigation/timeout cancelled the persist; the cookie is still set.
        }
    }
}

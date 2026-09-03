using System.Net;
using System.Net.Http.Json;
using MMCA.Common.UI.Services.Auth.Tokens;

namespace MMCA.Common.UI.Services.Preferences;

/// <summary>
/// Default <see cref="IUserPreferenceWriter"/>: PUTs the preference to <c>auth/preferences</c> via the
/// shared <c>"APIClient"</c> (which already attaches the bearer token + Accept-Language). No-ops when
/// there is no usable token and swallows transport errors, so persistence is strictly best-effort over
/// the cookie-based runtime channel (ADR-027 / ADR-028). Hosts without that endpoint (e.g. the Helpdesk
/// seed) simply do not register this writer.
/// <para>
/// Best-effort means the caller never learns the write failed, which makes a doomed request pure cost:
/// it cannot help the user and it still lands in failed-request telemetry. Both guards below exist to
/// keep a signed-out or stale session from spending one 401 per theme or culture toggle, which at low
/// traffic is enough on its own to trip a failed-request alert rule.
/// </para>
/// </summary>
/// <param name="httpClientFactory">Factory for the named <c>"APIClient"</c>.</param>
/// <param name="tokenStorageService">Supplies the token this writer decides against.</param>
public sealed class ApiUserPreferenceWriter(
    IHttpClientFactory httpClientFactory,
    ITokenStorageService tokenStorageService) : IUserPreferenceWriter
{
    /// <summary>Matches the token-storage skew, so this agrees with the layer that does the refreshing.</summary>
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(30);

    private sealed record UserPreferencesRequest(string? Culture, string? Theme);

    /// <summary>
    /// The token the API last rejected. Held for the lifetime of this scoped writer so a session the
    /// server has stopped accepting costs ONE failed request rather than one per toggle. Comparing the
    /// token itself rather than setting a latch means a fresh sign-in (a different token) resumes
    /// writing with no reset step and no staleness of its own.
    /// </summary>
    private string? _rejectedToken;

    /// <inheritdoc/>
    public async Task SaveAsync(string? culture, string? theme, CancellationToken cancellationToken = default)
    {
        var token = await tokenStorageService.GetAccessTokenAsync();

        // Anonymous users have no profile to persist to, and an expired token cannot acquire one: the
        // cookie is the only channel either way. IsFresh covers null and unreadable tokens too, so this
        // is also the anonymous guard.
        if (!JwtTokenInfo.IsFresh(token, ExpirySkew))
        {
            return;
        }

        // Still current and already refused once. A token can be unexpired and rejected anyway (revoked
        // session, rotated signing key, a user the API now treats as gone), so expiry alone is not
        // enough to predict this.
        if (string.Equals(token, _rejectedToken, StringComparison.Ordinal))
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

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _rejectedToken = token;
            }
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

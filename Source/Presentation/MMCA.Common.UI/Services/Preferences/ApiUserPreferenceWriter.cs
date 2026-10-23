using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MMCA.Common.UI.Services.Auth.Tokens;

namespace MMCA.Common.UI.Services.Preferences;

/// <summary>
/// Default <see cref="IUserPreferenceWriter"/>: PUTs the preference to <c>auth/preferences</c> via the
/// shared <c>"APIClient"</c> (whose handlers add Accept-Language), attaching the bearer token it read
/// itself on the request message. No-ops when there is no usable token and swallows transport errors,
/// so persistence is strictly best-effort over the cookie-based runtime channel (ADR-027 / ADR-028).
/// Hosts without that endpoint (e.g. the Helpdesk seed) simply do not register this writer.
/// <para>
/// The bearer goes on the request rather than being left to <c>AuthDelegatingHandler</c>: in Blazor
/// Server that handler resolves in a separate DI scope whose token store is empty, so relying on it
/// sends the write anonymous and earns a 401. The handler leaves an existing <c>Authorization</c>
/// header alone, so WASM and MAUI see no change. The header is set per request, never on the factory
/// client's shared default headers.
/// </para>
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

    private readonly Lock _gate = new();

    private sealed record UserPreferencesRequest(string? Culture, string? Theme);

    /// <summary>
    /// The token the API last rejected. Held for the lifetime of this scoped writer so a session the
    /// server has stopped accepting costs ONE failed request rather than one per toggle. Comparing the
    /// token itself rather than setting a latch means a fresh sign-in (a different token) resumes
    /// writing with no reset step and no staleness of its own.
    /// </summary>
    private string? _rejectedToken;

    /// <summary>The newest save, which the next one waits on before it sends.</summary>
    private Task _lastSave = Task.CompletedTask;

    /// <summary>
    /// How long a save waits for the one before it. Bounded so a hung earlier request delays a later
    /// save (and the culture switch's reload behind it) by a few seconds at most, never indefinitely.
    /// </summary>
    internal TimeSpan PreviousSaveWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <inheritdoc/>
    /// <remarks>
    /// Saves are sent one at a time, in call order. A theme toggle followed quickly by a language
    /// switch otherwise sent two concurrent PUTs, and the switcher force-reloads as soon as its own
    /// save returns: the reload cut off the theme PUT still in flight (WASM), or the two raced on the
    /// user row's concurrency token, so the server kept the old theme and the next sign-in applied it.
    /// Serializing here makes the switcher's existing await also drain the earlier save.
    /// </remarks>
    public Task SaveAsync(string? culture, string? theme, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var save = SaveAfterAsync(_lastSave, culture, theme, cancellationToken);
            _lastSave = save;
            return save;
        }
    }

    private async Task SaveAfterAsync(Task previous, string? culture, string? theme, CancellationToken cancellationToken)
    {
        // Best-effort either way: a fault, a timeout or a cancellation of the earlier save only ends the
        // wait, and this save goes ahead.
        await previous.WaitAsync(PreviousSaveWait, cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);

        await PutAsync(culture, theme, cancellationToken);
    }

    private async Task PutAsync(string? culture, string? theme, CancellationToken cancellationToken)
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
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri("auth/preferences", UriKind.Relative))
            {
                Content = JsonContent.Create(new UserPreferencesRequest(culture, theme)),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, cancellationToken);

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

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.UserNotifications;
using MMCA.Common.UI.Services.Auth;

namespace MMCA.Common.UI.Services.Notifications;

/// <summary>
/// HTTP service for the notification inbox WebAPI resource.
/// Every read that the API can scope carries the application's current scope key, resolved through
/// <see cref="INotificationScopeProvider"/>, so the inbox, the badge and a bulk mark-read all agree
/// on which notifications the user is looking at.
/// </summary>
/// <remarks>
/// The two reads go through <see cref="SendReadWithAuthRefreshAsync"/>: a badge poll or an inbox load
/// that lands on an access token the server has already rejected gets one forced token refresh and a
/// replay, instead of surfacing as an empty inbox or a blanked badge.
/// </remarks>
/// <param name="httpClientFactory">Factory for the named APIClient.</param>
/// <param name="tokenStorageService">Circuit-scoped access-token storage.</param>
/// <param name="scopeProvider">Resolves the application's current notification scope.</param>
/// <param name="tokenRefresher">
/// Optional: acquires a fresh access token after a 401. Hosts that register no refresher simply skip
/// the retry (the read then reports failure rather than a fabricated empty result).
/// </param>
public sealed class NotificationInboxService(
    IHttpClientFactory httpClientFactory,
    ITokenStorageService tokenStorageService,
    INotificationScopeProvider scopeProvider,
    ITokenRefresher? tokenRefresher = null)
    : AuthenticatedServiceBase(httpClientFactory, tokenStorageService), INotificationInboxUIService
{
    private const string Endpoint = "notifications/inbox";

    /// <inheritdoc />
    public async Task<PagedCollectionResult<UserNotificationDTO>?> GetInboxAsync(
        int pageNumber = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        string scopeQuery = await ScopeQueryAsync("&", cancellationToken);
        var url = new Uri(
            string.Create(CultureInfo.InvariantCulture, $"{Endpoint}?pageNumber={pageNumber}&pageSize={pageSize}{scopeQuery}"),
            UriKind.Relative);

        using HttpResponseMessage response = await SendReadWithAuthRefreshAsync(
            (client, ct) => client.GetAsync(url, ct), cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            await ServiceExceptionHelper.ThrowIfDomainExceptionAsync(response, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        return await response.Content
            .ReadFromJsonAsync<PagedCollectionResult<UserNotificationDTO>>(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int?> GetUnreadCountAsync(CancellationToken cancellationToken = default)
    {
        string scopeQuery = await ScopeQueryAsync("?", cancellationToken);
        var url = new Uri($"{Endpoint}/unread-count{scopeQuery}", UriKind.Relative);

        using HttpResponseMessage response = await SendReadWithAuthRefreshAsync(
            (client, ct) => client.GetAsync(url, ct), cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Unknown, deliberately NOT zero: reporting zero here let a rejected token or a transient
            // failure erase a badge that a real-time push had just incremented.
            return null;
        }

        return await response.Content.ReadFromJsonAsync<int>(cancellationToken);
    }

    /// <inheritdoc />
    public async Task MarkReadAsync(
        UserNotificationIdentifierType id,
        CancellationToken cancellationToken = default)
    {
        using HttpClient httpClient = await CreateAuthenticatedClientAsync();
        var url = string.Create(CultureInfo.InvariantCulture, $"{Endpoint}/{id}/read");

        HttpResponseMessage response = await RetryPolicy
            .ExecuteAsync(() => httpClient.PutAsync(new Uri(url, UriKind.Relative), content: null, cancellationToken));

        if (!response.IsSuccessStatusCode)
        {
            await ServiceExceptionHelper.ThrowIfDomainExceptionAsync(response, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }

    /// <inheritdoc />
    public async Task MarkAllReadAsync(CancellationToken cancellationToken = default)
    {
        string scopeQuery = await ScopeQueryAsync("?", cancellationToken);
        using HttpClient httpClient = await CreateAuthenticatedClientAsync();
        var url = $"{Endpoint}/read-all{scopeQuery}";

        HttpResponseMessage response = await RetryPolicy
            .ExecuteAsync(() => httpClient.PutAsync(new Uri(url, UriKind.Relative), content: null, cancellationToken));

        if (!response.IsSuccessStatusCode)
        {
            await ServiceExceptionHelper.ThrowIfDomainExceptionAsync(response, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }

    /// <summary>
    /// Runs one idempotent read under the shared retry policy and, when the API answers
    /// <c>401 Unauthorized</c>, makes a single attempt to acquire a fresh access token and replay it.
    /// </summary>
    /// <remarks>
    /// Only the reads use this: they are safe to replay, whereas a mark-read PUT is left with the
    /// existing single-shot behaviour. The response content is fully buffered before the send task
    /// completes, so the caller can still read it after the client that produced it is disposed.
    /// </remarks>
    /// <param name="send">Issues the request on the supplied client.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    private async Task<HttpResponseMessage> SendReadWithAuthRefreshAsync(
        Func<HttpClient, CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        using (HttpClient httpClient = await CreateAuthenticatedClientAsync())
        {
            response = await RetryPolicy.ExecuteAsync(() => send(httpClient, cancellationToken));
        }

        if (response.StatusCode != HttpStatusCode.Unauthorized || tokenRefresher is null)
        {
            return response;
        }

        string? refreshedToken = await TryAcquireRefreshedTokenAsync(cancellationToken);
        if (refreshedToken is null)
        {
            return response;
        }

        response.Dispose();

        using HttpClient retryClient = CreateClientWithToken(refreshedToken);
        return await RetryPolicy.ExecuteAsync(() => send(retryClient, cancellationToken));
    }

    /// <summary>
    /// Forces one access-token re-acquisition, returning <see langword="null"/> when the session can
    /// no longer be refreshed or when the host cannot refresh from here (SSR prerender).
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    private async Task<string?> TryAcquireRefreshedTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            string? token = await tokenRefresher!.AcquireAccessTokenAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch (InvalidOperationException)
        {
            // JS interop not available during SSR prerender: no refresh is possible here.
            return null;
        }
    }

    /// <summary>
    /// Renders the <c>scope</c> query fragment for the application's current scope, or an empty
    /// string when it is unscoped, which leaves the request byte-identical to the pre-scope one.
    /// </summary>
    /// <param name="separator">
    /// <c>"&amp;"</c> for a URL that already carries query parameters, <c>"?"</c> for one that does not.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    private async Task<string> ScopeQueryAsync(string separator, CancellationToken cancellationToken)
    {
        string? scopeKey = await scopeProvider.GetCurrentScopeKeyAsync(cancellationToken);

        return string.IsNullOrWhiteSpace(scopeKey)
            ? string.Empty
            : $"{separator}scope={Uri.EscapeDataString(scopeKey)}";
    }
}

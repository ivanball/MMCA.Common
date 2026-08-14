using System.Globalization;
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
public sealed class NotificationInboxService(
    IHttpClientFactory httpClientFactory,
    ITokenStorageService tokenStorageService,
    INotificationScopeProvider scopeProvider)
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
        using HttpClient httpClient = await CreateAuthenticatedClientAsync();
        var url = string.Create(CultureInfo.InvariantCulture, $"{Endpoint}?pageNumber={pageNumber}&pageSize={pageSize}{scopeQuery}");

        HttpResponseMessage response = await RetryPolicy
            .ExecuteAsync(() => httpClient.GetAsync(new Uri(url, UriKind.Relative), cancellationToken));

        if (!response.IsSuccessStatusCode)
        {
            await ServiceExceptionHelper.ThrowIfDomainExceptionAsync(response, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        return await response.Content
            .ReadFromJsonAsync<PagedCollectionResult<UserNotificationDTO>>(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> GetUnreadCountAsync(CancellationToken cancellationToken = default)
    {
        string scopeQuery = await ScopeQueryAsync("?", cancellationToken);
        using HttpClient httpClient = await CreateAuthenticatedClientAsync();
        var url = $"{Endpoint}/unread-count{scopeQuery}";

        HttpResponseMessage response = await RetryPolicy
            .ExecuteAsync(() => httpClient.GetAsync(new Uri(url, UriKind.Relative), cancellationToken));

        if (!response.IsSuccessStatusCode)
        {
            return 0;
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

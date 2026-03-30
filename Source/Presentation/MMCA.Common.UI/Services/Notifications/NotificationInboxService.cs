using System.Net.Http.Json;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.UserNotifications;
using MMCA.Common.UI.Services.Auth;

namespace MMCA.Common.UI.Services.Notifications;

/// <summary>
/// HTTP service for the notification inbox WebAPI resource.
/// </summary>
public sealed class NotificationInboxService(
    IHttpClientFactory httpClientFactory,
    ITokenStorageService tokenStorageService)
    : AuthenticatedServiceBase(httpClientFactory, tokenStorageService), INotificationInboxUIService
{
    private const string Endpoint = "notifications/inbox";

    /// <inheritdoc />
    public async Task<PagedCollectionResult<UserNotificationDTO>?> GetInboxAsync(
        int pageNumber = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        using HttpClient httpClient = await CreateAuthenticatedClientAsync();
        var url = $"{Endpoint}?pageNumber={pageNumber}&pageSize={pageSize}";

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
        using HttpClient httpClient = await CreateAuthenticatedClientAsync();
        var url = $"{Endpoint}/unread-count";

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
        var url = $"{Endpoint}/{id}/read";

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
        using HttpClient httpClient = await CreateAuthenticatedClientAsync();
        var url = $"{Endpoint}/read-all";

        HttpResponseMessage response = await RetryPolicy
            .ExecuteAsync(() => httpClient.PutAsync(new Uri(url, UriKind.Relative), content: null, cancellationToken));

        if (!response.IsSuccessStatusCode)
        {
            await ServiceExceptionHelper.ThrowIfDomainExceptionAsync(response, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }
}

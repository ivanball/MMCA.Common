using System.Globalization;
using System.Net.Http.Json;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.PushNotifications;
using MMCA.Common.UI.Services.Auth;

namespace MMCA.Common.UI.Services.Notifications;

/// <summary>
/// HTTP service for the <c>notifications</c> WebAPI resource.
/// Provides send and paginated history operations. A send is stamped with the application's current
/// scope key (<see cref="INotificationScopeProvider"/>), the same provider the inbox service reads
/// through, so what gets sent and what gets shown always resolve to one scope.
/// </summary>
public sealed class PushNotificationService(
    IHttpClientFactory httpClientFactory,
    ITokenStorageService tokenStorageService,
    INotificationScopeProvider scopeProvider)
    : EntityServiceBase<PushNotificationDTO, PushNotificationIdentifierType>(
        "notifications", httpClientFactory, tokenStorageService), IPushNotificationUIService
{
    /// <inheritdoc />
    public async Task<PushNotificationDTO?> SendAsync(
        SendPushNotificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A request that already names a scope keeps it: an explicit caller choice outranks the
        // ambient one. Only an unscoped request picks up whatever the app currently scopes to.
        SendPushNotificationRequest scopedRequest = request;
        if (string.IsNullOrWhiteSpace(scopedRequest.ScopeKey))
        {
            string? scopeKey = await scopeProvider.GetCurrentScopeKeyAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(scopeKey))
            {
                scopedRequest = request with { ScopeKey = scopeKey };
            }
        }

        return await SendRequestAsync<PushNotificationDTO>(
            httpClient => httpClient.PostAsJsonAsync(
                new Uri(Endpoint, UriKind.Relative),
                scopedRequest,
                cancellationToken),
            cancellationToken,
            throwIfNull: true);
    }

    /// <inheritdoc />
    public async Task<PagedCollectionResult<PushNotificationDTO>?> GetHistoryAsync(
        int pageNumber = 1,
        int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        var url = string.Create(CultureInfo.InvariantCulture, $"{Endpoint}?pageNumber={pageNumber}&pageSize={pageSize}");
        return await SendRequestAsync<PagedCollectionResult<PushNotificationDTO>>(
            httpClient => httpClient.GetAsync(new Uri(url, UriKind.Relative), cancellationToken),
            cancellationToken);
    }
}

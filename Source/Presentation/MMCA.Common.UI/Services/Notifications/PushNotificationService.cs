using System.Net.Http.Json;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.PushNotifications;
using MMCA.Common.UI.Services.Auth;

namespace MMCA.Common.UI.Services.Notifications;

/// <summary>
/// HTTP service for the <c>notifications</c> WebAPI resource.
/// Provides send and paginated history operations.
/// </summary>
public sealed class PushNotificationService(
    IHttpClientFactory httpClientFactory,
    ITokenStorageService tokenStorageService)
    : EntityServiceBase<PushNotificationDTO, PushNotificationIdentifierType>(
        "notifications", httpClientFactory, tokenStorageService), IPushNotificationUIService
{
    /// <inheritdoc />
    public async Task<PushNotificationDTO?> SendAsync(
        SendPushNotificationRequest request,
        CancellationToken cancellationToken = default) =>
        await SendRequestAsync<PushNotificationDTO>(
            httpClient => httpClient.PostAsJsonAsync(
                new Uri(Endpoint, UriKind.Relative),
                request,
                cancellationToken),
            cancellationToken,
            throwIfNull: true);

    /// <inheritdoc />
    public async Task<PagedCollectionResult<PushNotificationDTO>?> GetHistoryAsync(
        int pageNumber = 1,
        int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        var url = $"{Endpoint}?pageNumber={pageNumber}&pageSize={pageSize}";
        return await SendRequestAsync<PagedCollectionResult<PushNotificationDTO>>(
            httpClient => httpClient.GetAsync(new Uri(url, UriKind.Relative), cancellationToken),
            cancellationToken);
    }
}

using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// No-op live channel publisher. Registered as the default implementation so that DI resolves
/// successfully when push notifications are not configured. Downstream apps override this with
/// <see cref="SignalRLiveChannelPublisher"/> via <c>AddPushNotifications()</c>, or with their own
/// transport (e.g. a gRPC adapter that forwards to the host that maps the hub).
/// </summary>
public sealed class NullLiveChannelPublisher : ILiveChannelPublisher
{
    /// <inheritdoc />
    public Task PublishAsync(string channelKey, string eventName, string payloadJson, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

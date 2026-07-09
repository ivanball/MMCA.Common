using Microsoft.AspNetCore.SignalR;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Hubs;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// Publishes ephemeral live channel events to connected clients via a SignalR group send. Uses
/// <see cref="IHubContext{THub}"/> so it works from any host that maps <see cref="NotificationHub"/>;
/// when a Redis backplane is configured, group sends fan out across replicas.
/// </summary>
public sealed class SignalRLiveChannelPublisher(IHubContext<NotificationHub> hubContext) : ILiveChannelPublisher
{
    /// <inheritdoc />
    public async Task PublishAsync(string channelKey, string eventName, string payloadJson, CancellationToken cancellationToken = default) =>
        await hubContext.Clients
            .Group(channelKey)
            .SendAsync(NotificationHub.ReceiveChannelEventMethod, channelKey, eventName, payloadJson, cancellationToken)
            .ConfigureAwait(false);
}

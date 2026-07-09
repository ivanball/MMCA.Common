namespace MMCA.Common.Application.Interfaces.Infrastructure;

/// <summary>
/// Publishes ephemeral live events to a channel of connected clients (e.g. <c>event:1</c> or
/// <c>session:123</c>). Unlike <see cref="IPushNotificationSender"/>, channel events are not
/// persisted: clients that are not connected and subscribed at publish time never see them.
/// Infrastructure implementations may use SignalR groups, a message fan-out service, etc.
/// </summary>
public interface ILiveChannelPublisher
{
    /// <summary>Publishes an event to every client currently subscribed to the channel.</summary>
    /// <param name="channelKey">The channel key (e.g. <c>event:1</c>).</param>
    /// <param name="eventName">The application-defined event name (e.g. <c>poll.results-changed</c>).</param>
    /// <param name="payloadJson">The event payload as a JSON string.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task PublishAsync(string channelKey, string eventName, string payloadJson, CancellationToken cancellationToken = default);
}

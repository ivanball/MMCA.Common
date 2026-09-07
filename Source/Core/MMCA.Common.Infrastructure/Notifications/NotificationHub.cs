using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Infrastructure.Notifications.Push;

namespace MMCA.Common.Infrastructure.Notifications;

/// <summary>
/// SignalR hub for push notifications and live channel events. The hub itself stays thin: it maps
/// authenticated user connections and manages channel (group) membership. Notification delivery is
/// handled by <see cref="Notifications.Push.SignalRPushNotificationSender"/> and channel event delivery by
/// <see cref="Notifications.Live.SignalRLiveChannelPublisher"/>, both using <see cref="IHubContext{THub}"/>.
/// </summary>
/// <param name="settings">Push settings, including the channel-key shape pattern.</param>
/// <param name="joinAuthorizer">
/// Optional entitlement check consulted before a connection joins a channel. Optional and defaulted
/// so an existing host keeps working unchanged; a host that publishes anything to a channel that is
/// not public to every signed-in user must register one, because the shape pattern alone lets any
/// authenticated caller subscribe to any well-formed key.
/// </param>
[Authorize]
public sealed class NotificationHub(
    IOptions<PushNotificationSettings> settings,
    IChannelJoinAuthorizer? joinAuthorizer = null) : Hub
{
    /// <summary>The SignalR method name clients listen on to receive notifications.</summary>
    public const string ReceiveNotificationMethod = "ReceiveNotification";

    /// <summary>The SignalR method name clients listen on to receive ephemeral channel events.</summary>
    public const string ReceiveChannelEventMethod = "ReceiveChannelEvent";

    /// <summary>The hub method name clients invoke to join a channel.</summary>
    public const string JoinChannelMethod = "JoinChannel";

    /// <summary>The hub method name clients invoke to leave a channel.</summary>
    public const string LeaveChannelMethod = "LeaveChannel";

    private static readonly TimeSpan ChannelKeyMatchTimeout = TimeSpan.FromSeconds(1);

    // One pattern per host in practice; cached so join/leave do not recompile the regex per call.
    private static readonly ConcurrentDictionary<string, Regex> ChannelKeyRegexCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Adds the calling connection to a channel (SignalR group) so it receives events published via
    /// <see cref="Application.Interfaces.Infrastructure.Notifications.ILiveChannelPublisher"/> for that channel key.
    /// </summary>
    /// <param name="channelKey">The channel key, validated against <see cref="PushNotificationSettings.ChannelKeyPattern"/>.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="HubException">The channel key does not match the configured pattern.</exception>
    [HubMethodName(JoinChannelMethod)]
    public async Task JoinChannelAsync(string channelKey)
    {
        EnsureValidChannelKey(channelKey);
        await EnsureAuthorizedForChannelAsync(channelKey).ConfigureAwait(false);

        // The cancellation token comes from the connection rather than a method parameter: the hub
        // method signature is the client-visible RPC contract, bound by SignalR's dispatcher, so it
        // carries no CancellationToken argument (see CancellationTokenConventionTests' exemption).
        await Groups.AddToGroupAsync(Context.ConnectionId, channelKey, Context.ConnectionAborted)
            .ConfigureAwait(false);
    }

    /// <summary>Removes the calling connection from a channel (SignalR group).</summary>
    /// <param name="channelKey">The channel key, validated against <see cref="PushNotificationSettings.ChannelKeyPattern"/>.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="HubException">The channel key does not match the configured pattern.</exception>
    [HubMethodName(LeaveChannelMethod)]
    public async Task LeaveChannelAsync(string channelKey)
    {
        EnsureValidChannelKey(channelKey);

        // Same as JoinChannelAsync: the token is the connection's, not a parameter on the RPC contract.
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, channelKey, Context.ConnectionAborted)
            .ConfigureAwait(false);
    }

    // SECURITY: shape is not entitlement. Without an authorizer the hub keeps its historical
    // behaviour (any authenticated caller may join any well-formed key); with one, a caller who is
    // not entitled to the channel is refused before the group membership is created, so it never
    // receives a single published payload.
    private async Task EnsureAuthorizedForChannelAsync(string channelKey)
    {
        if (joinAuthorizer is null)
        {
            return;
        }

        var allowed = await joinAuthorizer
            .CanJoinAsync(Context.User, channelKey, Context.ConnectionAborted)
            .ConfigureAwait(false);

        if (!allowed)
        {
            throw new HubException("Not authorized for this channel.");
        }
    }

    private void EnsureValidChannelKey(string channelKey)
    {
        Regex regex = ChannelKeyRegexCache.GetOrAdd(
            settings.Value.ChannelKeyPattern,
            static pattern => new Regex(pattern, RegexOptions.None, ChannelKeyMatchTimeout));

        if (string.IsNullOrEmpty(channelKey) || !regex.IsMatch(channelKey))
        {
            throw new HubException("Invalid channel key.");
        }
    }
}

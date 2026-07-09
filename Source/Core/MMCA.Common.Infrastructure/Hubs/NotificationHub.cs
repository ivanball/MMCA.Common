using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.Infrastructure.Hubs;

/// <summary>
/// SignalR hub for push notifications and live channel events. The hub itself stays thin: it maps
/// authenticated user connections and manages channel (group) membership. Notification delivery is
/// handled by <see cref="Services.SignalRPushNotificationSender"/> and channel event delivery by
/// <see cref="Services.SignalRLiveChannelPublisher"/>, both using <see cref="IHubContext{THub}"/>.
/// </summary>
[Authorize]
public sealed class NotificationHub(IOptions<PushNotificationSettings> settings) : Hub
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
    /// <see cref="Application.Interfaces.Infrastructure.ILiveChannelPublisher"/> for that channel key.
    /// </summary>
    /// <param name="channelKey">The channel key, validated against <see cref="PushNotificationSettings.ChannelKeyPattern"/>.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="HubException">The channel key does not match the configured pattern.</exception>
    [HubMethodName(JoinChannelMethod)]
    public async Task JoinChannelAsync(string channelKey)
    {
        EnsureValidChannelKey(channelKey);
        await Groups.AddToGroupAsync(Context.ConnectionId, channelKey).ConfigureAwait(false);
    }

    /// <summary>Removes the calling connection from a channel (SignalR group).</summary>
    /// <param name="channelKey">The channel key, validated against <see cref="PushNotificationSettings.ChannelKeyPattern"/>.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="HubException">The channel key does not match the configured pattern.</exception>
    [HubMethodName(LeaveChannelMethod)]
    public async Task LeaveChannelAsync(string channelKey)
    {
        EnsureValidChannelKey(channelKey);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, channelKey).ConfigureAwait(false);
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

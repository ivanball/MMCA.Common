using MMCA.Common.Shared.Notifications;

namespace MMCA.Common.Infrastructure.Notifications.Push;

/// <summary>
/// Push notification settings bound from the <c>PushNotifications</c> configuration section.
/// </summary>
public sealed class PushNotificationSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "PushNotifications";

    /// <summary>Gets a value indicating whether push notifications are enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>Gets the SignalR hub endpoint path.</summary>
    public string HubPath { get; init; } = "/hubs/notifications";

    /// <summary>
    /// Gets the regular expression a channel key must match before a client may join or leave a
    /// channel via the notification hub. Guards SignalR group names against arbitrary client input.
    /// <para>
    /// The default is <see cref="NotificationScopeKey.Pattern"/>, the same constant
    /// <see cref="NotificationScopeKey.ForEvent"/> and <see cref="NotificationScopeKey.ForSession"/>
    /// format against, so the producer and the guard cannot drift apart. A host that overrides the
    /// pattern from configuration takes on that alignment itself.
    /// </para>
    /// </summary>
    public string ChannelKeyPattern { get; init; } = NotificationScopeKey.Pattern;

    /// <summary>
    /// Maximum simultaneous hub connections one authenticated user may hold on this replica.
    /// <c>0</c> disables the cap.
    /// </summary>
    /// <remarks>
    /// SEC-ADC-25. The hub is <c>[Authorize]</c> but had no <c>OnConnectedAsync</c> override, so a
    /// single valid attendee token could open unbounded WebSockets and exhaust the replica: live
    /// notifications then stop for everyone. The default is high enough for a person with several
    /// tabs and devices, and low enough that one token cannot exhaust a replica.
    /// </remarks>
    [System.ComponentModel.DataAnnotations.Range(0, 10_000)]
    public int MaxConnectionsPerUser { get; init; } = 20;
}

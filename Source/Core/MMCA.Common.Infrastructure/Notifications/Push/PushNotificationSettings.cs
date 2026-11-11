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

    /// <summary>
    /// Gets the SignalR hub endpoint path. The JWT bearer handlers accept the query-string
    /// <c>access_token</c> on this path (and under <c>/hubs</c>); a host moving the hub elsewhere
    /// must also set <c>RateLimiting:HubPathPrefixes</c>, which the rate limiter reads separately.
    /// The shipped client (<c>NotificationHubService</c> in MMCA.Common.UI) connects at the default
    /// <c>/hubs/notifications</c> and has no setting to follow a move, so a host that changes this
    /// path must also change the client, plus the gateway route, <c>RateLimiting:HubPathPrefixes</c>
    /// and the same-origin proxy exemption.
    /// </summary>
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
    /// single valid user token could open unbounded WebSockets and exhaust the replica: live
    /// notifications then stop for everyone. The default is high enough for a person with several
    /// tabs and devices, and low enough that one token cannot exhaust a replica.
    /// </remarks>
    [System.ComponentModel.DataAnnotations.Range(0, 10_000)]
    public int MaxConnectionsPerUser { get; init; } = 20;
    /// <summary>
    /// Gets a value indicating whether the server closes a hub connection when the access token it
    /// was authenticated with expires. Off by default.
    /// </summary>
    /// <remarks>
    /// A hub connection is authenticated once, when it opens, and otherwise keeps that identity for
    /// as long as it stays open: a user who is locked, loses a role or signs out everywhere keeps
    /// receiving pushes until the connection happens to drop. With this on, the connection ends with
    /// its token. The shipped client reconnects at once with a fresh token, so a signed-in user sees
    /// one reconnect per token lifetime, and a user whose sessions were revoked cannot obtain a token
    /// and is refused. The cut-off is bounded by <c>Jwt:AccessTokenExpirationMinutes</c>, not
    /// immediate. It works behind a non-sticky ingress with several replicas, because each replica
    /// enforces it on the connections it holds and no message has to reach them.
    /// </remarks>
    public bool CloseConnectionsOnTokenExpiry { get; init; }
}

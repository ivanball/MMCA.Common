namespace MMCA.Common.UI.Common.Settings;

/// <summary>
/// Strongly-typed options bound to the <c>"NotificationBell"</c> configuration section: how often the
/// unread badge re-reads the API, and how old its count may be before a navigation re-reads it.
/// <para>
/// Both numbers used to be compiled in (a hardcoded thirty-second timer, and a refresh on EVERY
/// navigation). Stating them here makes the badge's staleness a host decision: a deployment paying
/// per API call widens the poll, a deployment where an unread count must feel instant narrows it.
/// </para>
/// </summary>
public sealed class NotificationBellOptions
{
    /// <summary>Configuration section name used for binding.</summary>
    public static readonly string SectionName = "NotificationBell";

    /// <summary>
    /// How often the single active bell re-reads the authoritative unread count. The periodic read is
    /// the backstop behind the real-time push, not the primary path, so this is the budget for "how
    /// long a missed push may go unnoticed".
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How old the count may be for a navigation to accept it. A page change used to force an
    /// unconditional read, so a user clicking through five pages in ten seconds issued five reads for
    /// a number that had not moved; within this window the badge now keeps the count it has.
    /// </summary>
    public TimeSpan NavigationRefreshMaxAge { get; init; } = TimeSpan.FromSeconds(30);
}

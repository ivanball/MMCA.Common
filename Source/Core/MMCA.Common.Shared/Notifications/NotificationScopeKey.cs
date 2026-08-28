using System.Globalization;
using System.Text.RegularExpressions;

namespace MMCA.Common.Shared.Notifications;

/// <summary>
/// Canonical formatter and validator for a notification scope key (also called a channel key on
/// the SignalR join path): the <c>event:{id}</c> / <c>session:{id}</c> string that narrows a push
/// notification, or a live channel, to one subject.
/// </summary>
/// <remarks>
/// The format and the pattern that guards it live together on purpose. The pattern is the default
/// of <c>PushNotificationSettings.ChannelKeyPattern</c>, which the notification hub enforces before
/// a client may join a group; the format was hand-written at each call site, so a change to either
/// half could silently strand the other. Building every key through <see cref="ForEvent"/> or
/// <see cref="ForSession"/> keeps them in step, and formats the identifier under
/// <see cref="CultureInfo.InvariantCulture"/> so a culture with non-ASCII digits cannot produce a
/// key the pattern rejects.
/// </remarks>
public static partial class NotificationScopeKey
{
    /// <summary>The prefix of an event-scoped key.</summary>
    public const string EventPrefix = "event";

    /// <summary>The prefix of a session-scoped key.</summary>
    public const string SessionPrefix = "session";

    /// <summary>
    /// The regular expression a scope key must match. Shared with
    /// <c>PushNotificationSettings.ChannelKeyPattern</c>, whose default it supplies.
    /// </summary>
    public const string Pattern = "^(event|session):[0-9]+$";

    /// <summary>Builds the scope key for an event.</summary>
    /// <param name="eventId">The event identifier.</param>
    /// <returns>The key in the form <c>event:{id}</c>.</returns>
    public static string ForEvent(long eventId) =>
        string.Create(CultureInfo.InvariantCulture, $"{EventPrefix}:{eventId}");

    /// <summary>Builds the scope key for a session.</summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <returns>The key in the form <c>session:{id}</c>.</returns>
    public static string ForSession(long sessionId) =>
        string.Create(CultureInfo.InvariantCulture, $"{SessionPrefix}:{sessionId}");

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="scopeKey"/> matches <see cref="Pattern"/>.
    /// </summary>
    /// <param name="scopeKey">The scope key to check.</param>
    /// <returns><see langword="true"/> for a well-formed scope key; otherwise, <see langword="false"/>.</returns>
    public static bool IsValid(string? scopeKey) =>
        !string.IsNullOrEmpty(scopeKey) && ScopeKeyRegex.IsMatch(scopeKey);

    // ExplicitCapture only suppresses the unused capture of the "(event|session)" alternation; the
    // pattern text stays exactly the one PushNotificationSettings defaults to, and IsMatch is
    // unaffected by the option.
    [GeneratedRegex(Pattern, RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ScopeKeyRegex { get; }
}

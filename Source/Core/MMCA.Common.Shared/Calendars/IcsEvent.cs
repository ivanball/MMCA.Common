namespace MMCA.Common.Shared.Calendars;

/// <summary>
/// One calendar entry for <see cref="IcsCalendarBuilder"/>. Times are UTC by contract —
/// converting wall-clock event times (e.g. a session's start in the event's IANA time zone)
/// to UTC is the caller's job, which lets the builder emit <c>Z</c>-suffixed timestamps and
/// skip RFC 5545's error-prone VTIMEZONE machinery entirely.
/// </summary>
/// <param name="Uid">Globally unique, stable id (e.g. <c>session-42@myapp</c>); calendar apps use it to de-duplicate reimports.</param>
/// <param name="Summary">Entry title.</param>
/// <param name="StartsAtUtc">Start instant (UTC).</param>
/// <param name="EndsAtUtc">End instant (UTC).</param>
/// <param name="Description">Optional body text.</param>
/// <param name="Location">Optional location (room, venue address).</param>
public sealed record IcsEvent(
    string Uid,
    string Summary,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    string? Description = null,
    string? Location = null);

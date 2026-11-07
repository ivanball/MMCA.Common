namespace MMCA.Common.Shared.Auth.Responses;

/// <summary>
/// One row of a sign-in history: a single sign-in (one rotation chain of refresh sessions) from the
/// moment it opened to its last token refresh, and how it ended. A sign-in that rotated its refresh
/// token a hundred times is still one row.
/// <para>
/// <b>Sanitized on purpose</b>, like <see cref="RefreshSessionSummaryResponse"/>: no token hash and no
/// rotation link, only what a person recognizes a sign-in by.
/// </para>
/// </summary>
/// <param name="SignInId">
/// The sign-in's identifier: the id of the session that opened it (for a row written before sign-in
/// grouping shipped, the row's own id).
/// </param>
/// <param name="StartedAt">The UTC instant the sign-in opened (its earliest retained session).</param>
/// <param name="LastActiveAt">The UTC instant of its last token refresh (its newest session).</param>
/// <param name="IpAddress">The client IP recorded on the newest session, or <see langword="null"/>. Informational.</param>
/// <param name="UserAgent">The client user-agent recorded on the newest session, or <see langword="null"/>. Informational.</param>
/// <param name="EndedAt">
/// The UTC instant the sign-in ended (revoked, or its expiry), or <see langword="null"/> while it is live.
/// </param>
/// <param name="EndReason">
/// Why it ended (one of the <see cref="SignInEndReasons"/> values), or <see langword="null"/> while it
/// is live.
/// </param>
/// <param name="IsCurrent">
/// Whether this is the sign-in the calling access token belongs to (its <c>sid</c> claim). Always
/// <see langword="false"/> in an administrator's view of another account.
/// </param>
/// <param name="Kind">
/// What the row records. Every row is a <see cref="SignInHistoryEntryKind.SignIn"/> today; the kind is
/// the extension point for other account-access events that belong in the same history.
/// </param>
public sealed record SignInSummaryResponse(
    Guid SignInId,
    DateTime StartedAt,
    DateTime LastActiveAt,
    string? IpAddress,
    string? UserAgent,
    DateTime? EndedAt,
    string? EndReason,
    bool IsCurrent,
    SignInHistoryEntryKind Kind = SignInHistoryEntryKind.SignIn);

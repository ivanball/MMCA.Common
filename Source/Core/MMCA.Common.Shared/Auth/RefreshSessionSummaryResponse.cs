namespace MMCA.Common.Shared.Auth;

/// <summary>
/// One row of a user's "signed-in devices" list: a live refresh session, described in the terms a
/// person can recognize it by.
/// <para>
/// <b>Sanitized on purpose.</b> The token hash and the rotation link (<c>ReplacedByTokenHash</c>)
/// are deliberately absent. They are the material the reuse check runs on; putting either on a
/// response would hand every caller a queryable index of another session's credentials-at-rest for
/// no gain, since nothing a client does with a session needs anything but its id.
/// </para>
/// </summary>
/// <param name="SessionId">The session identifier, the value a per-device sign-out is addressed to.</param>
/// <param name="CreatedAt">The UTC instant the session was opened (when this device signed in).</param>
/// <param name="ExpiresAt">The UTC instant the session stops being usable even if never revoked.</param>
/// <param name="IpAddress">The client IP recorded at issue time, or <see langword="null"/>. Informational.</param>
/// <param name="UserAgent">The client user-agent recorded at issue time, or <see langword="null"/>. Informational.</param>
/// <param name="IsCurrent">
/// Whether this is the session the calling access token was minted for (its <c>sid</c> claim).
/// Always <see langword="false"/> for a caller whose token predates the <c>sid</c> claim, since
/// nothing then identifies the caller's own device.
/// </param>
public sealed record RefreshSessionSummaryResponse(
    Guid SessionId,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    string? IpAddress,
    string? UserAgent,
    bool IsCurrent);

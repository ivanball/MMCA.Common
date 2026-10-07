using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.Application.Auth;

/// <summary>
/// Configuration for multi-device refresh sessions. Bound from the <c>RefreshSessions</c>
/// configuration section; a host that omits the section gets the defaults.
/// </summary>
public sealed class RefreshSessionSettings
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "RefreshSessions";

    /// <summary>
    /// Gets a value indicating whether the <c>RefreshSessions</c> table belongs in this host's model
    /// and its retention sweep runs here. Defaults to <see langword="false"/> (the
    /// <c>Scheduler:Enabled</c> precedent): the service that owns identity sets it to
    /// <see langword="true"/> and every other service in a modular host leaves it alone, which is
    /// what keeps the table, its migrations and its sweep in exactly one database.
    /// <para>
    /// It gates the model, not the workflow. <see cref="AuthenticationServiceBase{TUser}"/> always
    /// issues, rotates and revokes sessions; this flag decides which database carries the rows.
    /// </para>
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Maximum live sessions one user may hold at once. Signing in on device number
    /// <c>MaxActiveSessionsPerUser + 1</c> revokes that user's oldest live session rather than
    /// refusing the login, so the table cannot grow without bound while a legitimate sign-in never
    /// fails. Ten covers a realistic device set (phone, tablet, two laptops, a few browsers) with room
    /// to spare.
    /// </summary>
    [Range(1, 1000)]
    public int MaxActiveSessionsPerUser { get; init; } = 10;

    /// <summary>
    /// Logical data source whose database holds the <c>RefreshSessions</c> table, for a host that
    /// splits its modules across databases and keeps Identity on a named source. The default is the
    /// engine's <c>Default</c> source, which is what a single-database host uses.
    /// <para>
    /// It answers two questions at once, and they must agree: which context maps the table (only the
    /// context instance whose physical source carries this name does, so the other databases in a
    /// multi-source host keep an unchanged model), and which context the shipped
    /// <c>IRefreshSessionStore</c> reads and writes through. Setting it to a source that does not
    /// exist fails loudly on the first session query rather than reading the wrong database. It is
    /// ignored for routing when the consumer ships its own entity configuration for the session
    /// entity, since the data-source registry then places it like any other entity.
    /// </para>
    /// </summary>
    [MinLength(1)]
    public string DataSourceName { get; init; } = "Default";

    /// <summary>
    /// How long a session row survives after it stops being usable, before the framework's retention
    /// sweep hard-deletes it. The age is measured from the instant the session died (its revocation,
    /// or its expiry when it was never revoked), so a live session is never a candidate no matter how
    /// old it is. Defaults to 30 days.
    /// <para>
    /// <b>It bounds reuse detection.</b> BR-206 catches a replayed refresh token by landing on its
    /// revoked row; once that row is swept, the same replay reads as an unknown token and fails alone
    /// instead of revoking the family. Thirty days is far past the seven-day refresh-token lifetime,
    /// so a token still capable of being replayed always has its row. Lower it only knowing that a
    /// window shorter than <c>Jwt:RefreshTokenExpirationDays</c> starts deleting rows whose tokens
    /// could still come back.
    /// </para>
    /// <para>
    /// Set to <c>0</c> to keep every row forever, which makes the table an operator's problem.
    /// </para>
    /// </summary>
    [Range(0, 3650)]
    public int RetentionDays { get; init; } = 30;

    /// <summary>
    /// How often, in hours, the retention sweep runs. Ignored when <see cref="RetentionDays"/> is
    /// <c>0</c>. Defaults to <c>6</c>, matching the outbox sweep: the table is small and the deadline
    /// is a retention window measured in days, so nothing is gained by sweeping more often.
    /// </summary>
    [Range(1, 168)]
    public int CleanupIntervalHours { get; init; } = 6;

    /// <summary>
    /// How long, in seconds, after a session was rotated a second presentation of its token is treated
    /// as a rotation race rather than token reuse. Two tabs, or one browser served by two replicas, can
    /// present the same refresh token a moment apart; the later request then finds the row already
    /// rotated (or loses the rotation claim) and is answered <c>409 Conflict</c>
    /// (<c>Auth.RefreshSuperseded</c>) without revoking anything, so the client retries with the
    /// winner's token. Defaults to <c>10</c>, matching the cookie refresher's rotation grace.
    /// <para>
    /// <b>It is a security trade-off.</b> A replay of a stolen token that arrives within this window of
    /// a legitimate rotation is answered 409 instead of revoking the user's whole session family
    /// (BR-206). Past the window the BR-206 answer applies unchanged, and only a row revoked as
    /// <c>Rotated</c> qualifies: a row already flagged as reuse still revokes the family however recent.
    /// Set to <c>0</c> to treat every rotated token that comes back as reuse.
    /// </para>
    /// </summary>
    [Range(0, 300)]
    public int ReuseGraceSeconds { get; init; } = 10;
}

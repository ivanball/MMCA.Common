using System.Security.Cryptography;
using System.Text;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Domain.Auth;

/// <summary>
/// One refresh-token session: a single device's right to mint access tokens for one user, held as a
/// hash of the issued refresh token (BR-205/206). A user has as many rows as they have signed-in
/// devices, so signing in on a phone no longer signs the same account out of a laptop.
/// <para>
/// <b>Hash at rest.</b> The plaintext refresh token exists only in the response that hands it to the
/// client; the store keeps <see cref="TokenHash"/>, so a database read cannot mint tokens. Lookups are
/// by hash, which is why the digest is unsalted and deterministic (<see cref="HashToken"/>).
/// </para>
/// <para>
/// <b>Rotation leaves a chain.</b> Using a session revokes it and records the successor in
/// <see cref="ReplacedByTokenHash"/>. Presenting an already-rotated token therefore lands on a revoked
/// row rather than on nothing, which is exactly the signal that a token was replayed: the whole family
/// is revoked in response (BR-206 reuse detection).
/// </para>
/// <para>
/// <b>Framework bookkeeping, not an aggregate.</b> Like <c>OutboxMessage</c> and
/// <c>AuditTrailEntry</c>, this is a flat record with no audit stamps, no soft-delete flag and no
/// concurrency token: rows are never deleted or edited except to be revoked, and no global query
/// filter should hide a revoked row from the reuse check. It is mapped only where a consumer opts in
/// (<c>ApplyRefreshSessionConfiguration</c>), because sessions belong to the Identity module's
/// database rather than to every data source.
/// </para>
/// </summary>
public sealed class RefreshSession
{
    /// <summary>Length of a hex-encoded SHA-256 digest, and so the exact width of <see cref="TokenHash"/>.</summary>
    public const int TokenHashLength = 64;

    /// <summary>Column width for <see cref="IpAddress"/> (fits an IPv4-mapped IPv6 literal).</summary>
    public const int IpAddressMaxLength = 45;

    /// <summary>Column width for <see cref="UserAgent"/>; longer values are truncated on capture.</summary>
    public const int UserAgentMaxLength = 512;

    /// <summary>Column width for <see cref="ReasonRevoked"/>.</summary>
    public const int ReasonRevokedMaxLength = 64;

    /// <summary>Reason recorded when a session is revoked because it was rotated on use.</summary>
    public const string ReasonRotated = "Rotated";

    /// <summary>Reason recorded when a session is revoked by an explicit sign-out.</summary>
    public const string ReasonSignedOut = "SignedOut";

    /// <summary>Reason recorded for every session revoked by reuse detection (BR-206).</summary>
    public const string ReasonReuseDetected = "ReuseDetected";

    /// <summary>Reason recorded when the per-user session cap evicts the oldest session.</summary>
    public const string ReasonSessionCap = "SessionCapExceeded";

    /// <summary>Gets the unique identifier for this session row.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Gets the user this session authenticates.</summary>
    public required UserIdentifierType UserId { get; init; }

    /// <summary>Gets the hex-encoded SHA-256 digest of the issued refresh token (never the token).</summary>
    public required string TokenHash { get; init; }

    /// <summary>Gets the UTC instant the session was issued.</summary>
    public required DateTime CreatedAt { get; init; }

    /// <summary>Gets the UTC instant the session stops being usable, even if never revoked.</summary>
    public required DateTime ExpiresAt { get; init; }

    /// <summary>Gets the UTC instant the session was revoked, or null while it is live.</summary>
    public DateTime? RevokedAt { get; private set; }

    /// <summary>
    /// Gets the hash of the session that replaced this one on rotation, or null when the session was
    /// revoked for any other reason. This is the link that makes a rotation chain walkable.
    /// </summary>
    public string? ReplacedByTokenHash { get; private set; }

    /// <summary>Gets why the session was revoked (one of the <c>Reason*</c> constants), or null.</summary>
    public string? ReasonRevoked { get; private set; }

    /// <summary>
    /// Gets the client IP recorded at issue time, when the caller supplied one. Informational: it
    /// identifies a session in a "your devices" list and gives an audit trail for a revocation. It is
    /// never part of a validation decision, so a mobile client changing networks is not signed out.
    /// </summary>
    public string? IpAddress { get; init; }

    /// <summary>Gets the client user-agent recorded at issue time, when the caller supplied one.</summary>
    public string? UserAgent { get; init; }

    /// <summary>Gets a value indicating whether the session has been revoked.</summary>
    public bool IsRevoked => RevokedAt is not null;

    /// <summary>Whether the session is neither revoked nor past <see cref="ExpiresAt"/> at the given instant.</summary>
    /// <param name="utcNow">The current UTC instant.</param>
    public bool IsActiveAt(DateTime utcNow) => !IsRevoked && ExpiresAt > utcNow;

    /// <summary>
    /// Creates a session for a freshly issued refresh token, hashing the token on the way in so the
    /// plaintext never reaches a property.
    /// </summary>
    /// <param name="userId">The user the token authenticates.</param>
    /// <param name="refreshToken">The plaintext refresh token handed to the client.</param>
    /// <param name="createdAt">The UTC issue instant.</param>
    /// <param name="expiresAt">The UTC expiry instant; must be after <paramref name="createdAt"/>.</param>
    /// <param name="ipAddress">Optional client IP; truncated to <see cref="IpAddressMaxLength"/>.</param>
    /// <param name="userAgent">Optional client user-agent; truncated to <see cref="UserAgentMaxLength"/>.</param>
    /// <returns>The session, or a validation failure.</returns>
    public static Result<RefreshSession> Create(
        UserIdentifierType userId,
        string refreshToken,
        DateTime createdAt,
        DateTime expiresAt,
        string? ipAddress = null,
        string? userAgent = null)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return Result.Failure<RefreshSession>(Error.Validation(
                "RefreshSession.TokenRequired",
                "A refresh session requires a refresh token.",
                nameof(Create)));
        }

        if (expiresAt <= createdAt)
        {
            return Result.Failure<RefreshSession>(Error.Validation(
                "RefreshSession.ExpiryInPast",
                "A refresh session must expire after it is created.",
                nameof(Create)));
        }

        return Result.Success(new RefreshSession
        {
            UserId = userId,
            TokenHash = HashToken(refreshToken),
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
            IpAddress = Truncate(ipAddress, IpAddressMaxLength),
            UserAgent = Truncate(userAgent, UserAgentMaxLength),
        });
    }

    /// <summary>
    /// Hashes a refresh token for storage and lookup: SHA-256 over the token's UTF-8 bytes, hex encoded
    /// in upper case.
    /// </summary>
    /// <remarks>
    /// The encoding is part of the contract, not an implementation detail: a consumer's data migration
    /// has to reproduce it exactly to carry existing tokens over, and SQL Server's
    /// <c>CONVERT(char(64), HASHBYTES('SHA2_256', CONVERT(varchar(max), Token)), 2)</c> is the
    /// byte-for-byte equivalent (style 2 emits upper-case hex with no <c>0x</c> prefix, and the
    /// varchar conversion is what makes the hashed bytes UTF-8 rather than UTF-16).
    /// </remarks>
    /// <param name="refreshToken">The plaintext refresh token.</param>
    /// <returns>The 64-character hex digest.</returns>
    public static string HashToken(string refreshToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
    }

    /// <summary>
    /// Revokes the session. Idempotent by refusal: revoking an already-revoked session is a failure
    /// rather than a silent overwrite, so the first reason and instant recorded are the ones kept.
    /// </summary>
    /// <param name="revokedAt">The UTC revocation instant.</param>
    /// <param name="reason">Why the session was revoked (one of the <c>Reason*</c> constants).</param>
    /// <param name="replacedByTokenHash">The successor session's hash when this is a rotation.</param>
    /// <returns>A success result, or a failure if the session was already revoked.</returns>
    public Result Revoke(DateTime revokedAt, string reason, string? replacedByTokenHash = null)
    {
        if (IsRevoked)
        {
            return Result.Failure(Error.Invariant(
                "RefreshSession.AlreadyRevoked",
                "The refresh session is already revoked.",
                nameof(Revoke)));
        }

        RevokedAt = revokedAt;
        ReasonRevoked = Truncate(reason, ReasonRevokedMaxLength);
        ReplacedByTokenHash = replacedByTokenHash;

        return Result.Success();
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];
}

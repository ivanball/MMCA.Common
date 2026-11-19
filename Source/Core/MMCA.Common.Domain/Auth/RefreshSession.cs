using System.Security.Cryptography;
using System.Text;
using MMCA.Common.Domain.Attributes;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Domain.Auth;

/// <summary>
/// One refresh-token session: a single device's right to mint access tokens for one user, held as a
/// hash of the issued refresh token (BR-205/206). A user has as many rows as they have signed-in
/// devices, so signing in on a phone does not sign the same account out of a laptop.
/// <para>
/// <b>Hash at rest.</b> The plaintext refresh token exists only in the response that hands it to the
/// client; the store keeps <see cref="TokenHash"/>, so a database read cannot mint tokens. Lookups are
/// by hash, which is why the digest is unsalted and deterministic (<see cref="HashToken"/>).
/// </para>
/// <para>
/// <b>Rotation leaves a chain.</b> Using a session revokes it and records the successor in
/// <see cref="ReplacedByTokenHash"/>. Presenting an already-rotated token therefore lands on a revoked
/// row rather than on nothing, which is exactly the signal that a token was replayed: the whole family
/// is revoked in response (BR-206 reuse detection), unless the token was rotated less than the reuse
/// grace (<c>RefreshSessions:ReuseGraceSeconds</c>) ago, which is treated as a race between sibling
/// requests and answered 409 with nothing revoked.
/// </para>
/// <para>
/// <b>Framework bookkeeping, not an aggregate.</b> Like <c>OutboxMessage</c> and
/// <c>AuditTrailEntry</c>, this is a flat record with no audit stamps, no soft-delete flag and no
/// concurrency token. A row is edited only to record the device a native client reports for it
/// (<see cref="RecordDevice"/>), to be revoked, or to be anonymized (<see cref="Anonymize"/>
/// clears the client metadata for an erasure) and is deleted only by the retention sweep once it
/// has been revoked or expired past the retention window; no global query filter should hide a
/// revoked row from the reuse check while it is kept. It is mapped only where a consumer opts in
/// (<c>ApplyRefreshSessionConfiguration</c>), because sessions belong to the Identity module's
/// database rather than to every data source.
/// </para>
/// <para>
/// <b>Personal data.</b> <see cref="IpAddress"/> and <see cref="UserAgent"/> identify the data subject's
/// device and network, and so do the seven <c>Device*</c> members a native client reports for its session
/// (<see cref="RecordDevice"/>), so all of them carry <c>[Pii]</c> and <see cref="Anonymize"/> clears them
/// for an erasure request (ADR-005). The token hashes, timestamps and revocation chain carry no personal
/// data and are kept, so reuse detection still works on an anonymized row.
/// </para>
/// </summary>
public sealed class RefreshSession : IAnonymizable
{
    /// <summary>Length of a hex-encoded SHA-256 digest, and so the exact width of <see cref="TokenHash"/>.</summary>
    public const int TokenHashLength = 64;

    /// <summary>Column width for <see cref="IpAddress"/> (fits an IPv4-mapped IPv6 literal).</summary>
    public const int IpAddressMaxLength = 45;

    /// <summary>Column width for <see cref="UserAgent"/>; longer values are truncated on capture.</summary>
    public const int UserAgentMaxLength = 512;

    /// <summary>Column width for <see cref="ReasonRevoked"/>.</summary>
    public const int ReasonRevokedMaxLength = 64;

    /// <summary>
    /// Column width for each of the seven <c>Device*</c> members (<see cref="RecordDevice"/>); longer
    /// values are truncated on capture.
    /// </summary>
    public const int DeviceFieldMaxLength = 256;

    /// <summary>Reason recorded when a session is revoked because it was rotated on use.</summary>
    public const string ReasonRotated = "Rotated";

    /// <summary>Reason recorded when a session is revoked by an explicit sign-out.</summary>
    public const string ReasonSignedOut = "SignedOut";

    /// <summary>Reason recorded for every session revoked by reuse detection (BR-206).</summary>
    public const string ReasonReuseDetected = "ReuseDetected";

    /// <summary>Reason recorded when the per-user session cap evicts the oldest session.</summary>
    public const string ReasonSessionCap = "SessionCapExceeded";

    /// <summary>Reason recorded when a session is revoked because its account was deleted and erased.</summary>
    public const string ReasonAccountDeleted = "AccountDeleted";

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
    /// Gets the sign-in this session belongs to: the <see cref="Id"/> of the session that opened it,
    /// carried unchanged to every successor on rotation (<see cref="CreateSuccessor"/>). It is what
    /// groups a rotation chain into one sign-in without walking <see cref="ReplacedByTokenHash"/>
    /// links. Null only on a row written before the column existed; such rows age out with the
    /// retention sweep, and a successor minted from one opens a fresh sign-in.
    /// </summary>
    public Guid? SignInId { get; private set; }

    /// <summary>
    /// Gets the client IP recorded at issue time, when the caller supplied one. Informational: it
    /// identifies a session in a "your devices" list and gives an audit trail for a revocation. It is
    /// never part of a validation decision, so a mobile client changing networks is not signed out.
    /// </summary>
    [Pii]
    public string? IpAddress { get; private set; }

    /// <summary>
    /// Gets the client user-agent recorded at issue time, when the caller supplied one. Marked <c>[Pii]</c>:
    /// alongside <see cref="IpAddress"/> it fingerprints the data subject's device.
    /// </summary>
    [Pii]
    public string? UserAgent { get; private set; }

    /// <summary>
    /// Gets the per-install identifier the native client reported for this session's device
    /// (<see cref="RecordDevice"/>), or null. Like every <c>Device*</c> member it is informational,
    /// never part of a validation decision, and carried to the successor on rotation.
    /// </summary>
    [Pii]
    public string? DeviceId { get; private set; }

    /// <summary>Gets the owner-assigned device name the native client reported, or null.</summary>
    [Pii]
    public string? DeviceName { get; private set; }

    /// <summary>Gets the device hardware model the native client reported, or null.</summary>
    [Pii]
    public string? DeviceModel { get; private set; }

    /// <summary>Gets the device manufacturer the native client reported, or null.</summary>
    [Pii]
    public string? DeviceManufacturer { get; private set; }

    /// <summary>Gets the device operating system the native client reported, or null.</summary>
    [Pii]
    public string? DevicePlatform { get; private set; }

    /// <summary>Gets the device form factor (phone, tablet, desktop) the native client reported, or null.</summary>
    [Pii]
    public string? DeviceFormFactor { get; private set; }

    /// <summary>Gets whether the device is physical or virtual, as the native client reported it, or null.</summary>
    [Pii]
    public string? DeviceType { get; private set; }

    /// <summary>Gets a value indicating whether the session has been revoked.</summary>
    public bool IsRevoked => RevokedAt is not null;

    /// <summary>Whether the session is neither revoked nor past <see cref="ExpiresAt"/> at the given instant.</summary>
    /// <param name="utcNow">The current UTC instant.</param>
    public bool IsActiveAt(DateTime utcNow) => !IsRevoked && ExpiresAt > utcNow;

    /// <summary>
    /// Creates a session for a freshly issued refresh token, hashing the token on the way in so the
    /// plaintext never reaches a property. The session opens a new sign-in (its
    /// <see cref="SignInId"/> is its own <see cref="Id"/>); a rotation uses
    /// <see cref="CreateSuccessor"/> instead, which keeps the sign-in.
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

        var session = new RefreshSession
        {
            UserId = userId,
            TokenHash = HashToken(refreshToken),
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
            IpAddress = Truncate(ipAddress, IpAddressMaxLength),
            UserAgent = Truncate(userAgent, UserAgentMaxLength),
        };
        session.SignInId = session.Id;

        return Result.Success(session);
    }

    /// <summary>
    /// Creates the session that replaces this one on rotation: same user, same sign-in
    /// (<see cref="SignInId"/>), new token. Linking the two (<see cref="Revoke"/> with the successor's
    /// hash) stays the store's rotation claim; this only mints the row.
    /// </summary>
    /// <param name="refreshToken">The plaintext successor refresh token handed to the client.</param>
    /// <param name="createdAt">The UTC issue instant.</param>
    /// <param name="expiresAt">The UTC expiry instant; must be after <paramref name="createdAt"/>.</param>
    /// <param name="ipAddress">Optional client IP; truncated to <see cref="IpAddressMaxLength"/>.</param>
    /// <param name="userAgent">Optional client user-agent; truncated to <see cref="UserAgentMaxLength"/>.</param>
    /// <returns>The successor session, or a validation failure.</returns>
    public Result<RefreshSession> CreateSuccessor(
        string refreshToken,
        DateTime createdAt,
        DateTime expiresAt,
        string? ipAddress = null,
        string? userAgent = null)
    {
        var successor = Create(UserId, refreshToken, createdAt, expiresAt, ipAddress, userAgent);
        if (successor.TryGetValue(out var session))
        {
            if (SignInId is { } signInId)
            {
                // A row written before SignInId existed has none to pass on, so its successor keeps the
                // sign-in it opened in Create: from here on the chain is grouped like any other.
                session.SignInId = signInId;
            }

            // The device does not change on a refresh, and the client reports it once per sign-in, so
            // the successor inherits what this session recorded (already truncated, so copied as is).
            session.DeviceId = DeviceId;
            session.DeviceName = DeviceName;
            session.DeviceModel = DeviceModel;
            session.DeviceManufacturer = DeviceManufacturer;
            session.DevicePlatform = DevicePlatform;
            session.DeviceFormFactor = DeviceFormFactor;
            session.DeviceType = DeviceType;
        }

        return successor;
    }

    /// <summary>
    /// Records the device metadata a native client reported for this session, replacing whatever was
    /// recorded before (a member the client leaves out is cleared, not kept). Each value is trimmed,
    /// truncated to <see cref="DeviceFieldMaxLength"/>, and stored as null when blank. Whether the
    /// session may still be described (it is the caller's own, and live) is the caller's check: this
    /// only stamps the row.
    /// </summary>
    /// <param name="device">The reported metadata.</param>
    /// <returns>A success result.</returns>
    public Result RecordDevice(DeviceMetadata device)
    {
        ArgumentNullException.ThrowIfNull(device);

        DeviceId = NormalizeDeviceField(device.DeviceId);
        DeviceName = NormalizeDeviceField(device.DeviceName);
        DeviceModel = NormalizeDeviceField(device.DeviceModel);
        DeviceManufacturer = NormalizeDeviceField(device.DeviceManufacturer);
        DevicePlatform = NormalizeDeviceField(device.DevicePlatform);
        DeviceFormFactor = NormalizeDeviceField(device.DeviceFormFactor);
        DeviceType = NormalizeDeviceField(device.DeviceType);

        return Result.Success();
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

    /// <summary>
    /// Erases the client metadata captured for the session (<see cref="IpAddress"/>,
    /// <see cref="UserAgent"/> and the seven <c>Device*</c> members). Idempotent: an already-anonymized
    /// session stays as it is and the call succeeds. The session's validity is untouched; revoking it
    /// is the sign-out path's job.
    /// </summary>
    /// <returns>A success result.</returns>
    public Result Anonymize()
    {
        IpAddress = null;
        UserAgent = null;
        DeviceId = null;
        DeviceName = null;
        DeviceModel = null;
        DeviceManufacturer = null;
        DevicePlatform = null;
        DeviceFormFactor = null;
        DeviceType = null;
        return Result.Success();
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];

    private static string? NormalizeDeviceField(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Truncate(value.Trim(), DeviceFieldMaxLength);
}

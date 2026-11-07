using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.Domain.Auth;

/// <summary>
/// One read-only view-as grant (ADR-137): a staff user was allowed to see the app as another account
/// for a bounded time, for a stated reason. The row is the audit record of the access (there is no
/// separate audit-event API), and it is what the viewed account's sign-in history shows as "Viewed by
/// support".
/// <para>
/// <b>Framework bookkeeping, not an aggregate.</b> Like <see cref="RefreshSession"/>, a flat record
/// with no audit stamps, no soft-delete flag and no concurrency token: a grant is inserted, possibly
/// ended once, and swept after the retention window. No global query filter may hide one from the
/// per-request check that a view-as token's grant is still live.
/// </para>
/// </summary>
public sealed class ViewAsGrant
{
    /// <summary>The shortest reason accepted, so "x" cannot stand in for a justification.</summary>
    public const int ReasonMinLength = 10;

    /// <summary>The longest reason accepted, and the column width.</summary>
    public const int ReasonMaxLength = 500;

    /// <summary>The longest display snapshot kept, and the column width (an email address fits).</summary>
    public const int DisplayMaxLength = 256;

    /// <summary>The longest a grant may last, whatever the access-token lifetime is.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(15);

    /// <summary>Gets the grant's identifier, carried by the view-as token as its <c>vag</c> claim.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Gets the staff user who is viewing (the token's <c>act_sub</c> claim).</summary>
    public UserIdentifierType StaffUserId { get; init; }

    /// <summary>Gets the account being viewed (the token's <c>sub</c> claim).</summary>
    public UserIdentifierType TargetUserId { get; init; }

    /// <summary>Gets the staff user's stated reason.</summary>
    public required string Reason { get; init; }

    /// <summary>Gets the UTC instant the grant was issued.</summary>
    public DateTime GrantedAt { get; init; }

    /// <summary>Gets the UTC instant the grant (and its token) expires.</summary>
    public DateTime ExpiresAt { get; init; }

    /// <summary>Gets the UTC instant the staff user ended the grant early, or <see langword="null"/>.</summary>
    public DateTime? EndedAt { get; private set; }

    /// <summary>
    /// Gets the staff user's email as it was when the grant was issued (the support access log's
    /// "who"), or <see langword="null"/> once that account is erased or when none was recorded.
    /// Personal data: it lives only as long as the row, and erasure blanks it
    /// (<see cref="ForgetDisplayOf"/>).
    /// </summary>
    public string? StaffDisplay { get; private set; }

    /// <summary>
    /// Gets the viewed account's email as it was when the grant was issued, or
    /// <see langword="null"/> once that account is erased or when none was recorded. Personal data,
    /// like <see cref="StaffDisplay"/>.
    /// </summary>
    public string? TargetDisplay { get; private set; }

    /// <summary>
    /// Creates a grant, validating the parties and the reason. The lifetime is clamped to
    /// <see cref="MaxLifetime"/>. No display snapshot is recorded.
    /// </summary>
    /// <param name="staffUserId">The staff user who will view.</param>
    /// <param name="targetUserId">The account to be viewed.</param>
    /// <param name="reason">The staff user's reason (10 to 500 characters after trimming).</param>
    /// <param name="grantedAt">The UTC instant of the grant.</param>
    /// <param name="lifetime">The requested lifetime; values above <see cref="MaxLifetime"/> are clamped.</param>
    /// <returns>The grant, or a validation failure.</returns>
    public static Result<ViewAsGrant> Create(
        UserIdentifierType staffUserId,
        UserIdentifierType targetUserId,
        string? reason,
        DateTime grantedAt,
        TimeSpan lifetime) =>
        Create(staffUserId, targetUserId, reason, grantedAt, lifetime, staffDisplay: null, targetDisplay: null);

    /// <summary>
    /// Creates a grant that also snapshots who viewed whom by email, so the support access log stays
    /// readable without a user lookup (and after an account is deleted, until erasure blanks it). The
    /// snapshots are folded to lower case (the form account emails are stored in) and cut to
    /// <see cref="DisplayMaxLength"/>; a blank one is recorded as <see langword="null"/>.
    /// </summary>
    /// <param name="staffUserId">The staff user who will view.</param>
    /// <param name="targetUserId">The account to be viewed.</param>
    /// <param name="reason">The staff user's reason (10 to 500 characters after trimming).</param>
    /// <param name="grantedAt">The UTC instant of the grant.</param>
    /// <param name="lifetime">The requested lifetime; values above <see cref="MaxLifetime"/> are clamped.</param>
    /// <param name="staffDisplay">The staff user's email, or <see langword="null"/>.</param>
    /// <param name="targetDisplay">The viewed account's email, or <see langword="null"/>.</param>
    /// <returns>The grant, or a validation failure.</returns>
    public static Result<ViewAsGrant> Create(
        UserIdentifierType staffUserId,
        UserIdentifierType targetUserId,
        string? reason,
        DateTime grantedAt,
        TimeSpan lifetime,
        string? staffDisplay,
        string? targetDisplay)
    {
        if (EqualityComparer<UserIdentifierType>.Default.Equals(staffUserId, targetUserId))
        {
            return Result.Failure<ViewAsGrant>(Error.Validation(
                "ViewAs.Self",
                "You cannot view the app as yourself.",
                nameof(targetUserId)));
        }

        var trimmed = reason?.Trim();
        if (trimmed is null || trimmed.Length < ReasonMinLength || trimmed.Length > ReasonMaxLength)
        {
            return Result.Failure<ViewAsGrant>(Error.Validation(
                "ViewAs.ReasonInvalid",
                "A reason of 10 to 500 characters is required.",
                nameof(reason)));
        }

        if (lifetime <= TimeSpan.Zero || lifetime > MaxLifetime)
        {
            lifetime = MaxLifetime;
        }

        return Result.Success(new ViewAsGrant
        {
            StaffUserId = staffUserId,
            TargetUserId = targetUserId,
            Reason = trimmed,
            GrantedAt = grantedAt,
            ExpiresAt = grantedAt.Add(lifetime),
            StaffDisplay = NormalizeDisplay(staffDisplay),
            TargetDisplay = NormalizeDisplay(targetDisplay),
        });
    }

    /// <summary>
    /// The form a display snapshot is stored in, and the form a search needle is folded to before it
    /// is compared with one: trimmed, lower case (invariant, character by character, so a match does
    /// not depend on a database collation), at most <see cref="DisplayMaxLength"/> characters, and
    /// <see langword="null"/> when blank.
    /// </summary>
    /// <param name="value">The raw display text.</param>
    /// <returns>The normalized text, or <see langword="null"/>.</returns>
    public static string? NormalizeDisplay(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var length = Math.Min(trimmed.Length, DisplayMaxLength);
        return string.Create(length, trimmed, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = char.ToLowerInvariant(source[i]);
            }
        });
    }

    /// <summary>Whether the grant is live at <paramref name="now"/>: not ended and not expired.</summary>
    /// <param name="now">The UTC instant to test.</param>
    /// <returns><see langword="true"/> while the grant may still be used.</returns>
    public bool IsActiveAt(DateTime now) => EndedAt is null && ExpiresAt > now;

    /// <summary>
    /// Where the grant stands at <paramref name="now"/>: <see cref="ViewAsGrantStatus.Ended"/> once the
    /// staff user ended it, otherwise <see cref="ViewAsGrantStatus.Live"/> until it expires and
    /// <see cref="ViewAsGrantStatus.Expired"/> after.
    /// </summary>
    /// <param name="now">The UTC instant to test.</param>
    /// <returns>The derived status.</returns>
    public ViewAsGrantStatus StatusAt(DateTime now)
    {
        if (EndedAt is not null)
        {
            return ViewAsGrantStatus.Ended;
        }

        return ExpiresAt > now ? ViewAsGrantStatus.Live : ViewAsGrantStatus.Expired;
    }

    /// <summary>Ends the grant at <paramref name="now"/>; ending an ended grant changes nothing.</summary>
    /// <param name="now">The UTC instant the staff user ended it.</param>
    public void End(DateTime now) => EndedAt ??= now;

    /// <summary>
    /// The erasure step for this row: blanks the display snapshot of <paramref name="userId"/> on the
    /// side (staff, viewed, or both) that account is on. The ids and the reason stay, so the access is
    /// still on record, without the erased account's email.
    /// </summary>
    /// <param name="userId">The account being erased.</param>
    /// <returns><see langword="true"/> when a snapshot was blanked.</returns>
    public bool ForgetDisplayOf(UserIdentifierType userId)
    {
        var changed = false;
        if (StaffUserId == userId && StaffDisplay is not null)
        {
            StaffDisplay = null;
            changed = true;
        }

        if (TargetUserId == userId && TargetDisplay is not null)
        {
            TargetDisplay = null;
            changed = true;
        }

        return changed;
    }
}

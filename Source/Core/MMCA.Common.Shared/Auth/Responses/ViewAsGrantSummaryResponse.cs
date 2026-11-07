namespace MMCA.Common.Shared.Auth.Responses;

/// <summary>
/// One row of the support access log (ADR-137): a view-as grant, who viewed whom, why, when, and how
/// it ended. No token or claim travels here, only the audit record.
/// </summary>
/// <param name="Id">The grant's identifier (a view-as token's <c>vag</c> claim).</param>
/// <param name="StaffUserId">The staff user who viewed.</param>
/// <param name="StaffDisplay">
/// The staff user's email as it was when the grant was issued, or <see langword="null"/> once that
/// account was erased (or for a grant issued before the snapshot shipped).
/// </param>
/// <param name="TargetUserId">The account that was viewed.</param>
/// <param name="TargetDisplay">
/// The viewed account's email as it was when the grant was issued, or <see langword="null"/> once that
/// account was erased (or for a grant issued before the snapshot shipped).
/// </param>
/// <param name="Reason">The staff user's stated reason, in full.</param>
/// <param name="GrantedAt">The UTC instant the grant was issued.</param>
/// <param name="ExpiresAt">The UTC instant the grant expires (or expired).</param>
/// <param name="EndedAt">The UTC instant the staff user ended it early, or <see langword="null"/>.</param>
/// <param name="Status">Where the grant stands at the time of the read.</param>
public sealed record ViewAsGrantSummaryResponse(
    Guid Id,
    UserIdentifierType StaffUserId,
    string? StaffDisplay,
    UserIdentifierType TargetUserId,
    string? TargetDisplay,
    string Reason,
    DateTime GrantedAt,
    DateTime ExpiresAt,
    DateTime? EndedAt,
    ViewAsGrantStatus Status);

using MMCA.Common.Domain.Auth;

namespace MMCA.Common.Application.Auth.ViewAs;

/// <summary>
/// Persistence for <see cref="ViewAsGrant"/> rows (ADR-137). The framework ships the EF
/// implementation over the Identity database; the in-memory one in <c>MMCA.Common.Testing</c> mirrors
/// it for workflow tests.
/// </summary>
public interface IViewAsGrantStore
{
    /// <summary>Inserts and saves a new grant.</summary>
    /// <param name="grant">The grant to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the row is saved.</returns>
    Task AddAsync(ViewAsGrant grant, CancellationToken cancellationToken = default);

    /// <summary>Returns the grant with this id, or <see langword="null"/>.</summary>
    /// <param name="grantId">The grant id (a view-as token's <c>vag</c> claim).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The grant, or <see langword="null"/> when no row has the id.</returns>
    Task<ViewAsGrant?> FindAsync(Guid grantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the staff user's grant at <paramref name="endedAt"/> and saves. The staff user is part of
    /// the lookup, so one staff user cannot end another's grant.
    /// </summary>
    /// <param name="grantId">The grant to end.</param>
    /// <param name="staffUserId">The staff user the grant must belong to.</param>
    /// <param name="endedAt">The UTC instant it ended.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the staff user's grant was found (ended now or before).</returns>
    Task<bool> EndAsync(Guid grantId, UserIdentifierType staffUserId, DateTime endedAt, CancellationToken cancellationToken = default);

    /// <summary>Returns the newest grants on one account, newest first.</summary>
    /// <param name="targetUserId">The viewed account.</param>
    /// <param name="maxCount">The most rows to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>At most <paramref name="maxCount"/> grants.</returns>
    Task<IReadOnlyList<ViewAsGrant>> GetRecentForTargetAsync(
        UserIdentifierType targetUserId,
        int maxCount,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes every grant issued before <paramref name="cutoff"/>.</summary>
    /// <param name="cutoff">The UTC retention boundary.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many rows were deleted.</returns>
    Task<int> DeleteIssuedBeforeAsync(DateTime cutoff, CancellationToken cancellationToken = default);
}

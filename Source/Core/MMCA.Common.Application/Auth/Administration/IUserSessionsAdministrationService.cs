using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.Application.Auth.Administration;

/// <summary>
/// The administrator's read-only view of another account's signed-in devices: that user's live
/// refresh sessions (not revoked, not expired), newest first. View only; it never revokes.
/// </summary>
public interface IUserSessionsAdministrationService
{
    /// <summary>
    /// Returns <paramref name="userId"/>'s live sessions, newest first, each with
    /// <see cref="RefreshSessionSummaryResponse.IsCurrent"/> false (an administrator is never on the
    /// viewed user's device). A user with no live sessions, or no such user, yields an empty list.
    /// </summary>
    /// <param name="userId">The account whose sessions to list.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The live sessions.</returns>
    Task<Result<IReadOnlyList<RefreshSessionSummaryResponse>>> GetSessionsAsync(
        UserIdentifierType userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the distinct users that hold at least one LIVE session (not revoked, not expired at the
    /// injected clock's now). A consumer's roster uses it as the "signed in" filter.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Each signed-in user exactly once, in no particular order.</returns>
    Task<Result<IReadOnlyList<UserIdentifierType>>> GetSignedInUserIdsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts the LIVE sessions of each requested user in one store query, for a roster page's
    /// "signed in" column. A requested user with no live session is ABSENT from the result (never a
    /// zero entry), and an empty request answers empty without querying the store.
    /// </summary>
    /// <param name="userIds">The users to count (typically one page of a roster); duplicates count once.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The live-session count per user that has at least one.</returns>
    Task<Result<IReadOnlyDictionary<UserIdentifierType, int>>> CountLiveSessionsAsync(
        IReadOnlyCollection<UserIdentifierType> userIds,
        CancellationToken cancellationToken = default);
}

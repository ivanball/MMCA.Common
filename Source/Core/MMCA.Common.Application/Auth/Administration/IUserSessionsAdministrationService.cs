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
}

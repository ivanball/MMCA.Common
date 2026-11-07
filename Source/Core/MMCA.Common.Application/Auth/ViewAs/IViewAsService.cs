using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.Application.Auth.ViewAs;

/// <summary>
/// Issues, ends and checks read-only view-as grants (ADR-137). A plain service rather than command
/// handlers: every command is refused under a view-as token, and the end call is made under one.
/// </summary>
public interface IViewAsService
{
    /// <summary>
    /// Grants <paramref name="staffUserId"/> a read-only view of <paramref name="targetUserId"/> and
    /// mints its token. Refused for the caller themselves, a missing or soft-deleted account, and an
    /// account that itself holds <c>users:view-as</c>.
    /// </summary>
    /// <param name="staffUserId">The staff user asking.</param>
    /// <param name="targetUserId">The account to view.</param>
    /// <param name="reason">The staff user's reason (10 to 500 characters).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The view-as token and its grant, or the refusal.</returns>
    Task<Result<ViewAsGrantResponse>> StartAsync(
        UserIdentifierType staffUserId,
        UserIdentifierType targetUserId,
        string? reason,
        CancellationToken cancellationToken = default);

    /// <summary>Ends the staff user's grant, so its token is refused on the next request.</summary>
    /// <param name="grantId">The grant (the token's <c>vag</c> claim).</param>
    /// <param name="staffUserId">The staff user (the token's <c>act_sub</c> claim).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success, or not found when the staff user holds no such grant.</returns>
    Task<Result> EndAsync(Guid grantId, UserIdentifierType staffUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the grant behind a view-as token is still live: it exists, names this staff user and
    /// this account, was not ended, and has not expired.
    /// </summary>
    /// <param name="grantId">The token's <c>vag</c> claim.</param>
    /// <param name="staffUserId">The token's <c>act_sub</c> claim.</param>
    /// <param name="targetUserId">The token's <c>sub</c> claim.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> while the token may still be used.</returns>
    Task<bool> IsGrantActiveAsync(
        Guid grantId,
        UserIdentifierType staffUserId,
        UserIdentifierType targetUserId,
        CancellationToken cancellationToken = default);
}

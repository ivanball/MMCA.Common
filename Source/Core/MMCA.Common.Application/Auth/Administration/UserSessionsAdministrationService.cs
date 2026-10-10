using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.Application.Auth.Administration;

/// <summary>
/// Default <see cref="IUserSessionsAdministrationService"/> over <see cref="IRefreshSessionStore"/>
/// and <see cref="TimeProvider"/>.
/// </summary>
/// <param name="refreshSessions">The refresh session store.</param>
/// <param name="timeProvider">The clock that decides which sessions are still live.</param>
public sealed class UserSessionsAdministrationService(
    IRefreshSessionStore refreshSessions,
    TimeProvider timeProvider) : IUserSessionsAdministrationService
{
    /// <inheritdoc />
    public Task<Result<IReadOnlyList<RefreshSessionSummaryResponse>>> GetSessionsAsync(
        UserIdentifierType userId,
        CancellationToken cancellationToken = default)
    {
        // TEST-FIRST STUB: the implementation lands in a separate change.
        ArgumentNullException.ThrowIfNull(refreshSessions);
        ArgumentNullException.ThrowIfNull(timeProvider);
        throw new NotImplementedException();
    }
}

using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Auth.SoftDeletedUsers;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Auth.Administration;

/// <summary>
/// The shared <see cref="IUserAdministrationService{TUserDto}"/> workflow over the app's own
/// <typeparamref name="TUser"/> aggregate (ADR-116): read one account, lock or unlock it, and move it
/// to another role. The app supplies only how its aggregate expresses a lock, a role and its DTO.
/// </summary>
/// <remarks>
/// <para>
/// <b>A lock takes effect at once, on every host.</b> Locking saves the account, writes the
/// soft-deleted user marker (<see cref="ISoftDeletedUserMarkerWriter"/>, ADR-047), so every host's
/// <c>SoftDeletedUserMiddleware</c> answers 401 to the access tokens the account already holds, and
/// then revokes its live refresh sessions, so no new access token is minted. Without the marker a
/// lock would leave each issued access token working until it expired. Unlocking an account that was
/// locked clears the marker, so a token minted after the unlock is not refused for the rest of the
/// marker's lifetime. An unlock that changed nothing leaves the marker alone: the same marker also
/// carries a revoked machine client's block on its service account (ADR-140), which a no-op unlock
/// must not lift.
/// </para>
/// <para>
/// The marker write and clear are <b>best effort</b>, the convention the erasure workflow uses: the
/// account change is already saved, so a cache fault is logged as a warning and the call still
/// succeeds. A failed write leaves the lock to the refresh-session revocation and the sign-in and
/// refresh gates (the account's access tokens then work until they expire); a failed clear keeps the
/// unlocked account's tokens refused until the marker expires.
/// </para>
/// <para>
/// <b>One role, not a set.</b> The contract takes a role list because an app may hold several; this
/// workflow serves an aggregate that stores exactly one, so a request carrying zero or two is a
/// validation failure (<see cref="CreateExactlyOneRoleError"/>) rather than something to silently
/// truncate. Assigning the role the account already holds is a no-op success: nothing is saved and
/// nothing is revoked, so re-submitting the same value does not sign the account out. A real change
/// revokes the live refresh sessions, because the role travels in the access token.
/// </para>
/// </remarks>
/// <typeparam name="TUser">The app's <c>User</c> aggregate.</typeparam>
/// <typeparam name="TUserDto">The app's administration-facing user DTO.</typeparam>
/// <param name="unitOfWork">Repository access to the user aggregate.</param>
/// <param name="refreshSessions">The account's refresh sessions, revoked on lock and on a role change.</param>
/// <param name="timeProvider">Clock for the lock instant and the revocation instant.</param>
/// <param name="markerWriter">
/// Writes and clears the soft-deleted user marker that blocks the account's issued access tokens.
/// <c>AddApplication</c> registers it.
/// </param>
/// <param name="logger">Receives one line per administrative change and the marker-failure warnings.</param>
public abstract partial class UserAdministrationServiceBase<TUser, TUserDto>(
    IUnitOfWork unitOfWork,
    IRefreshSessionStore refreshSessions,
    TimeProvider timeProvider,
    ISoftDeletedUserMarkerWriter markerWriter,
    ILogger logger) : IUserAdministrationService<TUserDto>
    where TUser : AuditableAggregateRootEntity<UserIdentifierType>
{
    /// <inheritdoc />
    public async Task<Result<TUserDto>> GetAsync(
        UserIdentifierType userId,
        CancellationToken cancellationToken = default)
    {
        var user = await unitOfWork
            .GetReadRepository<TUser, UserIdentifierType>()
            .GetByIdAsync(userId, cancellationToken)
            .ConfigureAwait(false);

        return user is null
            ? Result.Failure<TUserDto>(NotFound(nameof(GetAsync)))
            : Result.Success(MapToDto(user));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Loads the account (not found otherwise), applies the state through <see cref="ApplyLockState"/>
    /// (whose failure is returned before anything is saved), saves, then on a lock writes the
    /// soft-deleted user marker and revokes the live refresh sessions; on an unlock of an account
    /// that was locked it clears the marker.
    /// </remarks>
    public async Task<Result> SetLockedAsync(
        UserIdentifierType userId,
        bool locked,
        CancellationToken cancellationToken = default)
    {
        var user = await unitOfWork
            .GetRepository<TUser, UserIdentifierType>()
            .GetByIdAsync(userId, cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
        {
            return Result.Failure(NotFound(nameof(SetLockedAsync)));
        }

        var wasLocked = IsLocked(user);

        var applied = ApplyLockState(user, locked, timeProvider.GetUtcNow().UtcDateTime);
        if (applied.IsFailure)
        {
            return applied;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (locked)
        {
            // The marker first: it is what refuses the access tokens already issued, and the session
            // revocation below only stops new ones being minted.
            await WriteMarkerAsync(userId, cancellationToken).ConfigureAwait(false);
            await RefreshSessionRevocation.RevokeAllAsync(refreshSessions, timeProvider, userId, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (wasLocked)
        {
            await ClearMarkerAsync(userId, cancellationToken).ConfigureAwait(false);
        }

        LogLockChanged(logger, userId, locked);
        return Result.Success();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Refuses anything but exactly one role (<see cref="CreateExactlyOneRoleError"/>), validates the
    /// requested role (<see cref="ValidateRequestedRole"/>) before the account is loaded, loads the
    /// account (not found otherwise), applies the role (<see cref="ApplyRole"/>), and returns success
    /// with nothing saved when the stored role (<see cref="GetRole"/>) did not change. A change is
    /// saved and the live refresh sessions are revoked.
    /// </remarks>
    public async Task<Result> SetRolesAsync(
        UserIdentifierType userId,
        IReadOnlyList<string> roles,
        CancellationToken cancellationToken = default)
    {
        if (roles is not [var requestedRole])
        {
            return Result.Failure(CreateExactlyOneRoleError());
        }

        var validation = ValidateRequestedRole(requestedRole);
        if (validation.IsFailure)
        {
            return Result.Failure(validation.Errors);
        }

        var user = await unitOfWork
            .GetRepository<TUser, UserIdentifierType>()
            .GetByIdAsync(userId, cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
        {
            return Result.Failure(NotFound(nameof(SetRolesAsync)));
        }

        var previousRole = GetRole(user);

        var changed = ApplyRole(user, requestedRole);
        if (changed.IsFailure)
        {
            return changed;
        }

        var newRole = GetRole(user);
        if (string.Equals(previousRole, newRole, StringComparison.Ordinal))
        {
            // Already holds it: nothing to write, and nothing to revoke. Re-submitting the same
            // value must not sign the account out.
            return Result.Success();
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The role travels in the access token, so a change that left the live sessions alone would
        // keep the old capability set working until every issued token expired.
        await RefreshSessionRevocation.RevokeAllAsync(refreshSessions, timeProvider, userId, cancellationToken)
            .ConfigureAwait(false);

        LogRoleChanged(logger, userId, previousRole, newRole);
        return Result.Success();
    }

    /// <summary>Maps the account to the app's administration DTO.</summary>
    /// <param name="user">The account read by <see cref="GetAsync"/>.</param>
    /// <returns>The DTO.</returns>
    protected abstract TUserDto MapToDto(TUser user);

    /// <summary>
    /// Whether the account is locked now. Read before <see cref="ApplyLockState"/>, so an unlock
    /// clears the soft-deleted user marker only when it actually lifted a lock.
    /// </summary>
    /// <param name="user">The tracked account.</param>
    /// <returns><see langword="true"/> when the account is locked.</returns>
    protected abstract bool IsLocked(TUser user);

    /// <summary>
    /// Applies the lock state to the tracked account. A failure is returned to the caller as is,
    /// before anything is saved.
    /// </summary>
    /// <param name="user">The tracked account.</param>
    /// <param name="locked"><see langword="true"/> to lock, <see langword="false"/> to unlock.</param>
    /// <param name="utcNow">The instant of the change, for an aggregate that records when a lock began.</param>
    /// <returns>A success result, or the aggregate's refusal.</returns>
    protected abstract Result ApplyLockState(TUser user, bool locked, DateTime utcNow);

    /// <summary>The role the account holds, compared ordinally before and after <see cref="ApplyRole"/>.</summary>
    /// <param name="user">The tracked account.</param>
    /// <returns>The stored role.</returns>
    protected abstract string GetRole(TUser user);

    /// <summary>
    /// Applies the requested role to the tracked account. A failure is returned to the caller as is,
    /// before anything is saved.
    /// </summary>
    /// <param name="user">The tracked account.</param>
    /// <param name="role">The one requested role, as sent.</param>
    /// <returns>A success result, or the aggregate's refusal.</returns>
    protected abstract Result ApplyRole(TUser user, string role);

    /// <summary>
    /// Validates the requested role before the account is loaded (default: accepted, leaving the
    /// check to <see cref="ApplyRole"/>). Override it when an unknown role must be refused ahead of
    /// the not-found answer; the failure's errors are returned to the caller.
    /// </summary>
    /// <param name="role">The one requested role, as sent.</param>
    /// <returns>A success result, or the refusal.</returns>
    protected virtual Result ValidateRequestedRole(string role) => Result.Success();

    /// <summary>
    /// The failure returned when the request names zero roles or more than one, checked before
    /// anything else (typically a validation error with source <c>SetRolesAsync</c>).
    /// </summary>
    /// <returns>The app's error.</returns>
    /// <remarks>
    /// The app owns the whole error, code included, rather than only its wording: the API's error
    /// localizer (ADR-027) keys its text by code, so a code the framework emitted would take the
    /// framework's translation and replace the app's message at the HTTP edge.
    /// </remarks>
    protected abstract Error CreateExactlyOneRoleError();

    private static Error NotFound(string source) =>
        Error.NotFound.WithSource(source).WithTarget(typeof(TUser).Name);

    private async Task WriteMarkerAsync(UserIdentifierType userId, CancellationToken cancellationToken)
    {
        try
        {
            await markerWriter.MarkDeletedAsync(userId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The lock is saved: a cache fault must not report it as failed.
            LogMarkerWriteFailed(logger, userId, ex);
        }
    }

    private async Task ClearMarkerAsync(UserIdentifierType userId, CancellationToken cancellationToken)
    {
        try
        {
            await markerWriter.ClearMarkerAsync(userId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The unlock is saved: a cache fault must not report it as failed.
            LogMarkerClearFailed(logger, userId, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} lock state set to {Locked} by an administrator")]
    private static partial void LogLockChanged(ILogger logger, UserIdentifierType userId, bool locked);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} role changed from {PreviousRole} to {NewRole} by an administrator")]
    private static partial void LogRoleChanged(ILogger logger, UserIdentifierType userId, string previousRole, string newRole);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write the lock marker for user {UserId}; the account's already-issued access tokens keep working until they expire")]
    private static partial void LogMarkerWriteFailed(ILogger logger, UserIdentifierType userId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not clear the lock marker for user {UserId}; the unlocked account's access tokens are refused until the marker expires")]
    private static partial void LogMarkerClearFailed(ILogger logger, UserIdentifierType userId, Exception exception);
}

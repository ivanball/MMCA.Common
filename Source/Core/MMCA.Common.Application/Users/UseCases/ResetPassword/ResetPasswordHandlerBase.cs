using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Application.Users.UseCases.ResetPassword;

/// <summary>
/// The shared complete-a-password-reset workflow: redeem the single-use token, hash the new
/// password, let the aggregate apply its own invariants, persist, then clear the account's lockout
/// so the user can log in immediately with the new credential.
/// </summary>
/// <remarks>
/// <para>
/// Every rejection collapses to one <c>Auth.InvalidResetToken</c> error: an unknown, expired,
/// mismatched or attempt-capped token and a vanished account are indistinguishable to the caller, so
/// the endpoint reveals nothing about which addresses hold accounts or which tokens exist.
/// </para>
/// <para>
/// The command record stays app-side (<typeparamref name="TCommand"/>), matching the ChangePassword
/// hoist; the base reads it only through <see cref="ICommandWithRequest{TRequest}"/>.
/// </para>
/// </remarks>
/// <typeparam name="TUser">The app's <c>User</c> aggregate.</typeparam>
/// <typeparam name="TCommand">The app's reset-password command record.</typeparam>
public abstract class ResetPasswordHandlerBase<TUser, TCommand>(
    IUnitOfWork unitOfWork,
    IPasswordHasher passwordHasher,
    IPasswordResetTokenService tokenService,
    ILoginProtectionService loginProtection,
    ILogger logger) : ICommandHandler<TCommand, Result>
    where TUser : AuditableAggregateRootEntity<UserIdentifierType>, IPasswordChangeableUser
    where TCommand : ICommandWithRequest<ResetPasswordRequest>
{
    /// <summary>The unit of work (exposed for app-level extensions).</summary>
    protected IUnitOfWork UnitOfWork => unitOfWork;

    /// <summary>
    /// The name reported as the <c>source</c> of any error this handler returns. Defaults to the
    /// runtime type name, so an app subclass keeping the <c>ResetPasswordHandler</c> name reports
    /// that name in the error payload.
    /// </summary>
    protected virtual string HandlerName => GetType().Name;

    /// <inheritdoc />
    public async Task<Result> HandleAsync(
        TCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var request = command.Request;

        // The token is consumed here, before the save, by design: leaving it live until the write
        // succeeds opens a replay window in which the same token can be redeemed twice. A token
        // burned by a later invariant failure costs the user one more reset request.
        var consumed = await tokenService
            .ValidateAndConsumeAsync(request.Email, request.Token, cancellationToken)
            .ConfigureAwait(false);
        if (consumed.IsFailure)
        {
            UserUseCaseLog.PasswordResetRejected(logger, "token rejected");
            return Result.Failure(InvalidToken());
        }

        var userId = consumed.Value;
        var repository = unitOfWork.GetRepository<TUser, UserIdentifierType>();
        var user = await repository.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            UserUseCaseLog.PasswordResetRejected(logger, "account no longer resolvable");
            return Result.Failure(InvalidToken());
        }

        var (newHash, newSalt) = passwordHasher.HashPassword(request.NewPassword);
        var result = user.ChangePassword(newHash, newSalt);
        if (result.IsFailure)
        {
            return result;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // A user who reset the password because of a lockout must not stay locked out.
        await loginProtection.ResetFailedAttemptsAsync(request.Email, cancellationToken).ConfigureAwait(false);

        UserUseCaseLog.PasswordResetCompleted(logger, userId);
        return result;
    }

    private Error InvalidToken() =>
        Error.Unauthorized(
            "Auth.InvalidResetToken",
            "The reset link is invalid or has expired. Please request a new one.",
            HandlerName);
}

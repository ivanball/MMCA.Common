using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Auth.TwoFactor;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.Application.Users.UseCases.TwoFactor;

/// <summary>
/// The shared start-two-factor-enrollment workflow: mint a secret, store it WITHOUT activating the
/// second factor, and hand the account owner the key and its provisioning URI.
/// </summary>
/// <remarks>
/// <para>
/// <b>Starting is not enabling.</b> The stored secret is inert until
/// <see cref="ConfirmTwoFactorEnrollmentHandlerBase{TCommand}"/> sees a code minted from it. That two
/// step shape is what stops a user whose authenticator was never really keyed (a mistyped secret, a
/// scan that failed) from locking themselves out of their own account.
/// </para>
/// <para>
/// Re-running it on an account that is already enrolled deliberately fails rather than silently
/// re-keying: replacing a live secret would break the authenticator the user is currently signing in
/// with, so the path from one secret to another is disable, then enroll again.
/// </para>
/// <para>
/// The command record stays app-side (<typeparamref name="TCommand"/>), matching the ChangePassword
/// hoist: the base reads it only through <see cref="IUserScopedRequest"/>.
/// </para>
/// </remarks>
/// <typeparam name="TCommand">The app's begin-enrollment command record.</typeparam>
/// <param name="twoFactorService">Mints the secret and renders the provisioning URI.</param>
/// <param name="store">Persists the pending secret.</param>
/// <param name="logger">Logger for the enrollment audit line.</param>
public abstract class BeginTwoFactorEnrollmentHandlerBase<TCommand>(
    ITwoFactorService twoFactorService,
    ITwoFactorStore store,
    ILogger logger) : ICommandHandler<TCommand, Result<TwoFactorSetupResponse>>
    where TCommand : IUserScopedRequest
{
    /// <summary>
    /// The name reported as the <c>source</c> of any error this handler returns. Defaults to the
    /// runtime type name, so an app subclass reports its own handler name in the error payload.
    /// </summary>
    protected virtual string HandlerName => GetType().Name;

    /// <inheritdoc />
    public async Task<Result<TwoFactorSetupResponse>> HandleAsync(
        TCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var state = await store.GetAsync(command.UserId, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return Result.Failure<TwoFactorSetupResponse>(
                Error.NotFound.WithSource(HandlerName).WithTarget(nameof(ITwoFactorUserState)));
        }

        if (state.IsTwoFactorEnabled)
        {
            return Result.Failure<TwoFactorSetupResponse>(Error.Conflict(
                "Authentication.TwoFactorAlreadyEnabled",
                "Two-factor authentication is already enabled for this account. Disable it before enrolling again.",
                HandlerName));
        }

        var accountName = await ResolveAccountNameAsync(command.UserId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return Result.Failure<TwoFactorSetupResponse>(
                Error.NotFound.WithSource(HandlerName).WithTarget(nameof(ITwoFactorUserState)));
        }

        // A fresh secret every time enrollment is started, never a reuse of an abandoned one: an
        // interrupted enrollment must not leave a secret that somebody who saw the first QR code
        // could still key an authenticator with.
        var secret = twoFactorService.GenerateSecret();

        var stored = await store.StartEnrollmentAsync(command.UserId, secret, cancellationToken).ConfigureAwait(false);
        if (stored.IsFailure)
        {
            return Result.Failure<TwoFactorSetupResponse>(stored.Errors);
        }

        UserUseCaseLog.TwoFactorEnrollmentStarted(logger, command.UserId);

        return Result.Success(new TwoFactorSetupResponse(
            secret,
            twoFactorService.BuildProvisioningUri(secret, accountName)));
    }

    /// <summary>
    /// The label the account is shown under in the authenticator app, normally the user's email
    /// address. Implement with the app's own lookup; return <see langword="null"/> when the account
    /// cannot be resolved.
    /// </summary>
    /// <param name="userId">The enrolling account.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The account label, or <see langword="null"/>.</returns>
    protected abstract Task<string?> ResolveAccountNameAsync(
        UserIdentifierType userId,
        CancellationToken cancellationToken);
}

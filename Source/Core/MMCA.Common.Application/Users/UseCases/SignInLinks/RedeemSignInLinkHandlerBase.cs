using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.SignInLinks;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Shared.ValueObjects.Contact;

namespace MMCA.Common.Application.Users.UseCases.SignInLinks;

/// <summary>
/// The shared redeem-a-sign-in-link workflow (ADR-116): spend the single-use token, resolve the
/// account behind the address (creating it when an invitation allows), mark the address confirmed,
/// and open a session through <see cref="IAuthenticationService.SignInVerifiedEmailOwnerAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The token is spent FIRST</b>, before the account is even looked up, matching
/// <c>ConfirmEmailHandlerBase</c>. Looking the account up first would let a caller with no token learn
/// whether an address holds an account from how the request fails. Every rejection up to and including
/// the spend collapses to one <c>Authentication.InvalidSignInLink</c> error.
/// </para>
/// <para>
/// <b>Account creation is opt-in per app.</b> When the address has no account and the link was an
/// invitation carrying a <see cref="SignInLinkProfile"/>, the workflow calls
/// <see cref="ProvisionUser"/>; the default refuses, so an app that has not overridden it never
/// creates accounts through this path. A plain sign-in link never creates one.
/// </para>
/// <para>
/// <b>Redeeming confirms the address.</b> The link proves the caller reads the mailbox, which is the
/// proof email confirmation asks for, so an <see cref="IEmailConfirmableUser"/> that was unconfirmed is
/// confirmed in the same save.
/// </para>
/// </remarks>
/// <typeparam name="TUser">The app's <c>User</c> aggregate.</typeparam>
/// <typeparam name="TCommand">The app's redeem-sign-in-link command record.</typeparam>
/// <param name="unitOfWork">The unit of work the user aggregate is loaded and saved through.</param>
/// <param name="tokenService">Redeems the single-use sign-in token.</param>
/// <param name="authenticationService">Opens the session once the account is resolved.</param>
/// <param name="settings">The bound sign-in-link settings.</param>
/// <param name="logger">Logger for the sign-in-link audit lines.</param>
public abstract partial class RedeemSignInLinkHandlerBase<TUser, TCommand>(
    IUnitOfWork unitOfWork,
    ISignInLinkTokenService tokenService,
    IAuthenticationService authenticationService,
    IOptions<SignInLinkSettings> settings,
    ILogger logger) : ICommandHandler<TCommand, Result<AuthenticationResponse>>
    where TUser : AuditableAggregateRootEntity<UserIdentifierType>
    where TCommand : IRedeemSignInLinkCommand
{
    /// <summary>The unit of work (exposed for app-level extensions).</summary>
    protected IUnitOfWork UnitOfWork => unitOfWork;

    /// <summary>The bound sign-in-link settings.</summary>
    protected SignInLinkSettings Settings => settings.Value;

    /// <summary>
    /// The name reported as the <c>source</c> of any error this handler returns. Defaults to the
    /// runtime type name.
    /// </summary>
    protected virtual string HandlerName => GetType().Name;

    /// <inheritdoc />
    public async Task<Result<AuthenticationResponse>> HandleAsync(TCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var request = command.Request;

        if (!Settings.Enabled)
        {
            return Rejected("sign-in links not enabled");
        }

        if (!Email.Create(request.Email).TryGetValue(out var email))
        {
            return Rejected("malformed address");
        }

        var consumed = await tokenService
            .ValidateAndConsumeAsync(email.Value, request.Token, cancellationToken)
            .ConfigureAwait(false);
        if (!consumed.TryGetValue(out var grant))
        {
            return Rejected("token rejected");
        }

        var resolved = await ResolveAccountAsync(email, grant, request, cancellationToken).ConfigureAwait(false);
        if (!resolved.TryGetValue(out var user))
        {
            return Result.Failure<AuthenticationResponse>(resolved.Errors);
        }

        await OnEmailOwnershipProvedAsync(user, grant, cancellationToken).ConfigureAwait(false);

        var session = await authenticationService
            .SignInVerifiedEmailOwnerAsync(user.Id, command.IpAddress, command.UserAgent, cancellationToken)
            .ConfigureAwait(false);

        if (session.IsSuccess)
        {
            SignedInByLink(logger, user.Id, grant.Purpose);
        }

        return session;
    }

    /// <summary>
    /// Resolves the account behind <paramref name="email"/> without tracking it. Implement with the
    /// app's own no-tracking lookup; return <see langword="null"/> when no account matches.
    /// </summary>
    /// <param name="email">The normalized address from the request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching account, or <see langword="null"/>.</returns>
    protected abstract Task<TUser?> FindUntrackedByEmailAsync(Email email, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the account for an invited address that has none yet. The default refuses, so an app
    /// creates accounts through sign-in links only by overriding this.
    /// </summary>
    /// <param name="email">The invited address.</param>
    /// <param name="profile">The name the invitation carried.</param>
    /// <param name="request">
    /// The redeem request. An override that records Terms of Service acceptance reads
    /// <see cref="RedeemSignInLinkRequest.AcceptedTerms"/> here, exactly as registration does.
    /// </param>
    /// <returns>
    /// The new, NOT yet persisted account (the workflow adds and saves it, with its address
    /// confirmed), or a failure that is returned to the caller as is.
    /// </returns>
    protected virtual Result<TUser> ProvisionUser(Email email, SignInLinkProfile profile, RedeemSignInLinkRequest request) =>
        Result.Failure<TUser>(SignInLinkErrors.InvalidLink(HandlerName));

    /// <summary>
    /// Post-commit side effect for an account <see cref="ProvisionUser"/> created (publish the
    /// app's registration event, say). Default: none.
    /// </summary>
    /// <param name="user">The saved account.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the side effect has run.</returns>
    protected virtual Task OnUserProvisionedAsync(TUser user, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// Runs after every successful redemption, for a new account and an existing one alike, once the
    /// address is confirmed and saved (link the address's pending records to the account, say).
    /// Default: none.
    /// </summary>
    /// <param name="user">The resolved account.</param>
    /// <param name="grant">What the redeemed link granted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the side effect has run.</returns>
    protected virtual Task OnEmailOwnershipProvedAsync(TUser user, SignInLinkGrant grant, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    private async Task<Result<TUser>> ResolveAccountAsync(
        Email email,
        SignInLinkGrant grant,
        RedeemSignInLinkRequest request,
        CancellationToken cancellationToken)
    {
        var repository = unitOfWork.GetRepository<TUser, UserIdentifierType>();
        var untracked = await FindUntrackedByEmailAsync(email, cancellationToken).ConfigureAwait(false);

        if (untracked is null)
        {
            if (grant.Profile is null)
            {
                SignInLinkRejected(logger, "no account for the address");
                return Result.Failure<TUser>(SignInLinkErrors.InvalidLink(HandlerName));
            }

            var provisioned = ProvisionUser(email, grant.Profile, request);
            if (!provisioned.TryGetValue(out var created))
            {
                return provisioned;
            }

            var confirmed = ConfirmIfNeeded(created);
            if (confirmed.IsFailure)
            {
                return Result.Failure<TUser>(confirmed.Errors);
            }

            await repository.AddAsync(created, cancellationToken).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await OnUserProvisionedAsync(created, cancellationToken).ConfigureAwait(false);

            AccountProvisioned(logger, created.Id);
            return Result.Success(created);
        }

        var user = await repository.GetByIdAsync(untracked.Id, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            SignInLinkRejected(logger, "account no longer resolvable");
            return Result.Failure<TUser>(SignInLinkErrors.InvalidLink(HandlerName));
        }

        if (user is IEmailConfirmableUser { IsEmailConfirmed: false })
        {
            var confirmed = ConfirmIfNeeded(user);
            if (confirmed.IsFailure)
            {
                return Result.Failure<TUser>(confirmed.Errors);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return Result.Success(user);
    }

    private static Result ConfirmIfNeeded(TUser user) =>
        user is IEmailConfirmableUser { IsEmailConfirmed: false } confirmable
            ? confirmable.ConfirmEmail()
            : Result.Success();

    private Result<AuthenticationResponse> Rejected(string reason)
    {
        SignInLinkRejected(logger, reason);
        return Result.Failure<AuthenticationResponse>(SignInLinkErrors.InvalidLink(HandlerName));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} signed in with a sign-in link ({Purpose})")]
    private static partial void SignedInByLink(ILogger logger, UserIdentifierType userId, SignInLinkPurpose purpose);

    [LoggerMessage(Level = LogLevel.Information, Message = "Account {UserId} created from an invitation link")]
    private static partial void AccountProvisioned(ILogger logger, UserIdentifierType userId);

    // No address and no account id: the endpoint answers identically whether or not the address exists.
    [LoggerMessage(Level = LogLevel.Information, Message = "Sign-in link redemption not actioned ({Reason})")]
    private static partial void SignInLinkRejected(ILogger logger, string reason);
}

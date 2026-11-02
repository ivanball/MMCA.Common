using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.SignInLinks;
using MMCA.Common.Application.Interfaces.Infrastructure.Mail;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.Users.Emails;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.ValueObjects.Contact;

namespace MMCA.Common.Application.Users.UseCases.SignInLinks;

/// <summary>
/// The shared email-me-a-sign-in-link workflow (ADR-116): resolve the account behind the address,
/// mint a short-lived single-use token, and email the link. Every outcome returns
/// <see cref="Result.Success()"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Anti-enumeration is the whole point of the success-always rule</b>, exactly as in
/// <c>ForgotPasswordHandlerBase</c>: a disabled feature, a malformed address, an address with no
/// account, a throttled request and a failed send all log and report success, so the response carries
/// no signal about which addresses hold accounts.
/// </para>
/// <para>
/// This path never creates an account: an address with none gets no email. Creating the account is
/// what an invitation does, and an invitation is issued by the app with
/// <see cref="SignInLinkPurpose.Invite"/> through <see cref="ISignInLinkTokenService"/> directly.
/// </para>
/// </remarks>
/// <typeparam name="TUser">The app's <c>User</c> aggregate.</typeparam>
/// <typeparam name="TCommand">The app's send-sign-in-link command record.</typeparam>
/// <param name="unitOfWork">The unit of work the lookup override reaches a read repository through.</param>
/// <param name="tokenService">Issues the single-use sign-in token.</param>
/// <param name="emailSender">Sends the sign-in email.</param>
/// <param name="settings">The bound sign-in-link settings.</param>
/// <param name="logger">Logger for the sign-in-link audit lines.</param>
public abstract partial class SendSignInLinkHandlerBase<TUser, TCommand>(
    IUnitOfWork unitOfWork,
    ISignInLinkTokenService tokenService,
    IEmailSender emailSender,
    IOptions<SignInLinkSettings> settings,
    ILogger logger) : ICommandHandler<TCommand, Result>
    where TUser : AuditableAggregateRootEntity<UserIdentifierType>
    where TCommand : ICommandWithRequest<SendSignInLinkRequest>
{
    /// <summary>The unit of work (exposed so the lookup override can reach a read repository).</summary>
    protected IUnitOfWork UnitOfWork => unitOfWork;

    /// <summary>The bound sign-in-link settings.</summary>
    protected SignInLinkSettings Settings => settings.Value;

    /// <inheritdoc />
    public async Task<Result> HandleAsync(TCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!Settings.Enabled || string.IsNullOrWhiteSpace(Settings.SignInUrl))
        {
            SignInLinkRejected(logger, "sign-in links not enabled");
            return Result.Success();
        }

        var emailResult = Email.Create(command.Request.Email);
        if (!emailResult.TryGetValue(out var email))
        {
            SignInLinkRejected(logger, "malformed address");
            return Result.Success();
        }

        var user = await FindUntrackedByEmailAsync(email, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            SignInLinkRejected(logger, "no account for the address");
            return Result.Success();
        }

        var tokenResult = await tokenService
            .IssueAsync(email.Value, SignInLinkPurpose.SignIn, profile: null, cancellationToken)
            .ConfigureAwait(false);
        if (!tokenResult.TryGetValue(out var token))
        {
            SignInLinkRejected(logger, "request throttled");
            return Result.Success();
        }

        // Non-null: SignInUrl was checked above.
        string link = Settings.ComposeLink(email.Value, token)!;

        try
        {
            var (subject, body) = UserEmailResources.InRecipientCulture(
                user,
                () => (ComposeSubject(), ComposeBody(link)));
            await emailSender.SendAsync(email.Value, subject, body, isHtml: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The token is already issued and still valid, so the user can retry. Reporting the send
            // failure to the caller would be an oracle.
            SignInLinkEmailFailed(logger, ex, user.Id);
            return Result.Success();
        }

        SignInLinkRequested(logger, user.Id);
        return Result.Success();
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
    /// The subject line of the sign-in email, in the recipient's culture (both compose members run with
    /// the current UI culture set to the account's stored preference). Override to rebrand.
    /// </summary>
    /// <returns>The subject line.</returns>
    protected virtual string ComposeSubject() => UserEmailResources.Get("SignInLink.Subject");

    /// <summary>The HTML body of the sign-in email, in the recipient's culture. Override to rebrand.</summary>
    /// <param name="signInLink">The single-use link.</param>
    /// <returns>The HTML body to send.</returns>
    protected virtual string ComposeBody(string signInLink)
    {
        ArgumentNullException.ThrowIfNull(signInLink);

        string minutes = Settings.SignInLifetimeMinutes.ToString(CultureInfo.InvariantCulture);

        return $"<p>{UserEmailResources.Get("SignInLink.Intro")}</p>"
            + $"<p><a href=\"{WebUtility.HtmlEncode(signInLink)}\">{UserEmailResources.Get("SignInLink.LinkText")}</a></p>"
            + $"<p>{UserEmailResources.Plural("SignInLink.Expiry", Settings.SignInLifetimeMinutes, minutes)}</p>"
            + $"<p>{UserEmailResources.Get("SignInLink.Ignore")}</p>";
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sign-in link requested for user {UserId}; sign-in email sent")]
    private static partial void SignInLinkRequested(ILogger logger, UserIdentifierType userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sign-in link email could not be sent for user {UserId}; the issued token stays valid")]
    private static partial void SignInLinkEmailFailed(ILogger logger, Exception exception, UserIdentifierType userId);

    // No address and no account id: the endpoint answers identically whether or not the address exists.
    [LoggerMessage(Level = LogLevel.Information, Message = "Sign-in link request not actioned ({Reason})")]
    private static partial void SignInLinkRejected(ILogger logger, string reason);
}

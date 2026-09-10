using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.EmailConfirmation;
using MMCA.Common.Application.Interfaces.Infrastructure.Mail;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.ValueObjects.Contact;

namespace MMCA.Common.Application.Users.UseCases.EmailConfirmation;

/// <summary>
/// The shared send-a-confirmation-link workflow: resolve the account behind the address, mint a
/// single-use token, and email it. Every outcome returns <see cref="Result.Success()"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Anti-enumeration is the whole point of the success-always rule</b>, exactly as in
/// <c>ForgotPasswordHandlerBase</c>: a malformed address, an address with no account, an address that
/// is already confirmed, a throttled request and a failed send all log and report success, so the
/// response carries no signal about which addresses hold accounts. Only the request validator can
/// produce a 400, and it only inspects the shape of the address.
/// </para>
/// <para>
/// The email sender is the framework's existing <see cref="IEmailSender"/>; no new abstraction ships
/// with this workflow.
/// </para>
/// </remarks>
/// <typeparam name="TUser">The app's <c>User</c> aggregate.</typeparam>
/// <typeparam name="TCommand">The app's send-confirmation command record.</typeparam>
/// <param name="unitOfWork">The unit of work the lookup override reaches a read repository through.</param>
/// <param name="tokenService">Issues the single-use confirmation token.</param>
/// <param name="emailSender">Sends the confirmation email.</param>
/// <param name="settings">The bound email-confirmation settings.</param>
/// <param name="logger">Logger for the confirmation audit lines.</param>
public abstract class SendEmailConfirmationHandlerBase<TUser, TCommand>(
    IUnitOfWork unitOfWork,
    IEmailConfirmationTokenService tokenService,
    IEmailSender emailSender,
    IOptions<EmailConfirmationSettings> settings,
    ILogger logger) : ICommandHandler<TCommand, Result>
    where TUser : AuditableAggregateRootEntity<UserIdentifierType>
    where TCommand : ICommandWithRequest<SendEmailConfirmationRequest>
{
    /// <summary>The unit of work (exposed so the lookup override can reach a read repository).</summary>
    protected IUnitOfWork UnitOfWork => unitOfWork;

    /// <summary>The bound email-confirmation settings.</summary>
    protected EmailConfirmationSettings Settings => settings.Value;

    /// <inheritdoc />
    public async Task<Result> HandleAsync(TCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var emailResult = Email.Create(command.Request.Email);
        if (emailResult.IsFailure)
        {
            UserUseCaseLog.EmailConfirmationRejected(logger, "malformed address");
            return Result.Success();
        }

        var email = emailResult.Value!;
        var user = await FindUntrackedByEmailAsync(email, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            UserUseCaseLog.EmailConfirmationRejected(logger, "no account for the address");
            return Result.Success();
        }

        // Already confirmed is a no-op, not a re-send: a live token for a confirmed address is of no
        // use to the owner and is one more redeemable secret in flight.
        if (IsAlreadyConfirmed(user))
        {
            UserUseCaseLog.EmailConfirmationRejected(logger, "address already confirmed");
            return Result.Success();
        }

        var tokenResult = await tokenService.IssueAsync(email.Value, user.Id, cancellationToken).ConfigureAwait(false);
        if (tokenResult.IsFailure)
        {
            UserUseCaseLog.EmailConfirmationRejected(logger, "request throttled");
            return Result.Success();
        }

        var token = tokenResult.Value!;

        try
        {
            await emailSender.SendAsync(
                email.Value,
                ComposeSubject(),
                ComposeBody(ComposeConfirmationLink(email.Value, token), token),
                isHtml: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The token is already issued and still valid, so the user can retry. Reporting the send
            // failure to the caller would be an oracle.
            UserUseCaseLog.EmailConfirmationEmailFailed(logger, ex, user.Id);
            return Result.Success();
        }

        UserUseCaseLog.EmailConfirmationRequested(logger, user.Id);
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
    /// Whether the account's address is already confirmed. The default reads
    /// <c>IEmailConfirmableUser</c> when the aggregate implements it, and otherwise answers false, so
    /// an app that has not adopted the contract still gets a working send workflow.
    /// </summary>
    /// <param name="user">The resolved account.</param>
    /// <returns><see langword="true"/> when no confirmation is needed.</returns>
    protected virtual bool IsAlreadyConfirmed(TUser user) =>
        user is Domain.Auth.IEmailConfirmableUser { IsEmailConfirmed: true };

    /// <summary>The subject line of the confirmation email. Override to localize or rebrand.</summary>
    /// <returns>The subject line.</returns>
    protected virtual string ComposeSubject() => "Confirm your email address";

    /// <summary>
    /// The body of the confirmation email. The default carries the link when one can be composed AND
    /// the raw token, because clients without deep linking (the MAUI head) need the token typed into
    /// the confirmation page by hand.
    /// </summary>
    /// <param name="confirmationLink">The prefilled URL, or <see langword="null"/> when none is configured.</param>
    /// <param name="token">The raw single-use token.</param>
    /// <returns>The HTML body to send.</returns>
    protected virtual string ComposeBody(string? confirmationLink, string token)
    {
        string minutes = Settings.TokenLifetimeMinutes.ToString(CultureInfo.InvariantCulture);
        string linkBlock = confirmationLink is null
            ? string.Empty
            : $"<p><a href=\"{WebUtility.HtmlEncode(confirmationLink)}\">Confirm your email address</a></p>";

        return "<p>Please confirm this address so we know we can reach you.</p>"
            + linkBlock
            + $"<p>Your confirmation code is <strong>{WebUtility.HtmlEncode(token)}</strong>. It expires in {minutes} minutes and can be used once.</p>"
            + "<p>If you did not create an account, you can ignore this message.</p>";
    }

    /// <summary>
    /// Builds the prefilled confirmation URL, or returns <see langword="null"/> when
    /// <see cref="EmailConfirmationSettings.ConfirmationUrl"/> is unconfigured (the email then degrades
    /// to the token alone rather than shipping a broken link).
    /// </summary>
    /// <param name="email">The normalized address the token was issued for.</param>
    /// <param name="token">The raw single-use token.</param>
    /// <returns>The confirmation URL, or <see langword="null"/>.</returns>
    /// <remarks>
    /// SECURITY: the address and the single-use token ride in the URL FRAGMENT, not the query string,
    /// for the reason documented on <c>ForgotPasswordHandlerBase.ComposeResetLink</c>: a fragment is
    /// never sent to a server, so the live token stays out of ingress access logs, out of request
    /// telemetry, and out of any <c>Referer</c> the page emits.
    /// </remarks>
    protected virtual string? ComposeConfirmationLink(string email, string token) =>
        string.IsNullOrWhiteSpace(Settings.ConfirmationUrl)
            ? null
            : $"{Settings.ConfirmationUrl}#email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
}

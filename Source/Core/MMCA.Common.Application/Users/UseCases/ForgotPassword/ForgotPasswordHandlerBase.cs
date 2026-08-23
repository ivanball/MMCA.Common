using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.ValueObjects;

namespace MMCA.Common.Application.Users.UseCases.ForgotPassword;

/// <summary>
/// The shared start-a-password-reset workflow: resolve the account behind the address, mint a
/// single-use token, and email it. Every outcome returns <see cref="Result.Success()"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Anti-enumeration is the whole point of the success-always rule.</b> A malformed address, an
/// address with no account, a throttled request and a failed send all log and report success, so the
/// response body and status code carry no signal about which addresses are registered. Only the
/// request validator can produce a 400, and it only inspects the shape of the address.
/// </para>
/// <para>
/// The command record stays app-side (<typeparamref name="TCommand"/>), matching the ChangePassword
/// hoist: the base reads it only through <see cref="ICommandWithRequest{TRequest}"/>. Resolving an
/// account by email is the one app-specific step (each app's <c>User</c> stores the address
/// differently), so it is the single abstract member.
/// </para>
/// </remarks>
/// <typeparam name="TUser">The app's <c>User</c> aggregate.</typeparam>
/// <typeparam name="TCommand">The app's forgot-password command record.</typeparam>
public abstract class ForgotPasswordHandlerBase<TUser, TCommand>(
    IUnitOfWork unitOfWork,
    IPasswordResetTokenService tokenService,
    IEmailSender emailSender,
    IOptions<PasswordResetSettings> settings,
    ILogger logger) : ICommandHandler<TCommand, Result>
    where TUser : AuditableAggregateRootEntity<UserIdentifierType>
    where TCommand : ICommandWithRequest<ForgotPasswordRequest>
{
    /// <summary>The unit of work (exposed so the lookup override can reach a read repository).</summary>
    protected IUnitOfWork UnitOfWork => unitOfWork;

    /// <summary>The bound password-reset settings.</summary>
    protected PasswordResetSettings Settings => settings.Value;

    /// <inheritdoc />
    public async Task<Result> HandleAsync(
        TCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var emailResult = Email.Create(command.Request.Email);
        if (emailResult.IsFailure)
        {
            UserUseCaseLog.PasswordResetRejected(logger, "malformed address");
            return Result.Success();
        }

        var email = emailResult.Value!;
        var user = await FindUntrackedByEmailAsync(email, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            UserUseCaseLog.PasswordResetRejected(logger, "no account for the address");
            return Result.Success();
        }

        var tokenResult = await tokenService.IssueAsync(email.Value, user.Id, cancellationToken).ConfigureAwait(false);
        if (tokenResult.IsFailure)
        {
            UserUseCaseLog.PasswordResetRejected(logger, "request throttled");
            return Result.Success();
        }

        var token = tokenResult.Value!;

        try
        {
            await emailSender.SendAsync(
                email.Value,
                ComposeSubject(),
                ComposeBody(ComposeResetLink(email.Value, token), token),
                isHtml: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The token is already issued and still valid, so the user can retry (or use a link from
            // a later request). Reporting the send failure to the caller would be an oracle.
            UserUseCaseLog.PasswordResetEmailFailed(logger, ex, user.Id);
            return Result.Success();
        }

        UserUseCaseLog.PasswordResetRequested(logger, user.Id);
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

    /// <summary>The subject line of the reset email. Override to localize or rebrand.</summary>
    /// <returns>The subject line.</returns>
    protected virtual string ComposeSubject() => "Reset your password";

    /// <summary>
    /// The body of the reset email. The default carries the reset link when one can be composed AND
    /// the raw token, because clients without deep linking (the MAUI head) need the token typed into
    /// the reset page by hand.
    /// </summary>
    /// <param name="resetLink">The prefilled reset URL, or <see langword="null"/> when no reset URL is configured.</param>
    /// <param name="token">The raw single-use token.</param>
    /// <returns>The HTML body to send.</returns>
    protected virtual string ComposeBody(string? resetLink, string token)
    {
        string minutes = Settings.TokenLifetimeMinutes.ToString(CultureInfo.InvariantCulture);
        string linkBlock = resetLink is null
            ? string.Empty
            : $"<p><a href=\"{WebUtility.HtmlEncode(resetLink)}\">Reset your password</a></p>";

        return $"<p>We received a request to reset your password.</p>"
            + linkBlock
            + $"<p>Your reset code is <strong>{WebUtility.HtmlEncode(token)}</strong>. It expires in {minutes} minutes and can be used once.</p>"
            + "<p>If you did not request this, you can ignore this message: your password has not changed.</p>";
    }

    /// <summary>
    /// Builds the prefilled reset URL, or returns <see langword="null"/> when
    /// <see cref="PasswordResetSettings.ResetUrl"/> is unconfigured (the email then degrades to the
    /// token alone rather than shipping a broken link).
    /// </summary>
    /// <param name="email">The normalized address the token was issued for.</param>
    /// <param name="token">The raw single-use token.</param>
    /// <returns>The reset URL, or <see langword="null"/>.</returns>
    protected virtual string? ComposeResetLink(string email, string token) =>
        string.IsNullOrWhiteSpace(Settings.ResetUrl)
            ? null
            : $"{Settings.ResetUrl}?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
}

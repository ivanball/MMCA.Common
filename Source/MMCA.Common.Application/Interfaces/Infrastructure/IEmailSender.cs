namespace MMCA.Common.Application.Interfaces.Infrastructure;

/// <summary>
/// Sends email notifications. Infrastructure implementations may use SMTP, SendGrid, etc.
/// </summary>
public interface IEmailSender
{
    /// <summary>Sends an email to a specific recipient.</summary>
    /// <param name="to">The recipient email address.</param>
    /// <param name="subject">The email subject.</param>
    /// <param name="body">The email body (HTML or plain text).</param>
    /// <param name="isHtml">Whether the body is HTML. Defaults to <see langword="false"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SendAsync(string to, string subject, string body, bool isHtml = false, CancellationToken cancellationToken = default);

    /// <summary>Sends an email to a default/system recipient (e.g. admin notifications).</summary>
    /// <param name="subject">The email subject.</param>
    /// <param name="body">The email body.</param>
    /// <param name="isHtml">Whether the body is HTML. Defaults to <see langword="false"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SendAsync(string subject, string body, bool isHtml = false, CancellationToken cancellationToken = default);
}

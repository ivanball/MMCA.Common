using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.Infrastructure.Mail;

/// <summary>
/// Sends emails via SMTP using settings from <see cref="SmtpSettings"/>.
/// Each call creates a new <see cref="SmtpClient"/> and disposes it after sending.
/// </summary>
public sealed class SmtpEmailSender(IOptions<SmtpSettings> smtpOptions) : IEmailSender
{
    private readonly SmtpSettings _smtpSettings = smtpOptions.Value;

    /// <inheritdoc />
    public async Task SendAsync(string to, string subject, string body, bool isHtml = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(to);
        ArgumentException.ThrowIfNullOrEmpty(subject);
        ArgumentException.ThrowIfNullOrEmpty(body);

#pragma warning disable S5332 // EnableSsl is driven by SmtpSettings.EnableSsl (config); local dev targets MailDev, which does not offer TLS
        using var smtpClient = new SmtpClient(_smtpSettings.Host, _smtpSettings.Port)
        {
            Credentials = new NetworkCredential(_smtpSettings.Username, _smtpSettings.Password),
            EnableSsl = _smtpSettings.EnableSsl
        };
#pragma warning restore S5332

        using var message = new MailMessage(_smtpSettings.From, to, subject, body)
        {
            IsBodyHtml = isHtml
        };

        await smtpClient.SendMailAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends an email to the default recipient configured in SMTP settings.
    /// </summary>
    /// <param name="subject">The email subject.</param>
    /// <param name="body">The email body.</param>
    /// <param name="isHtml">Whether the body is HTML.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous send operation.</returns>
    public Task SendAsync(string subject, string body, bool isHtml = false, CancellationToken cancellationToken = default)
        => SendAsync(_smtpSettings.To, subject, body, isHtml, cancellationToken);
}

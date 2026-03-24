using System.Net;
using System.Net.Mail;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// Sends emails via SMTP using settings from <see cref="ISmtpSettings"/>.
/// Each call creates a new <see cref="SmtpClient"/> and disposes it after sending.
/// </summary>
public sealed class SmtpEmailSender(ISmtpSettings smtpSettings) : IEmailSender
{
    private readonly string _host = smtpSettings.Host;
    private readonly int _port = smtpSettings.Port;
    private readonly string _username = smtpSettings.Username;
    private readonly string _password = smtpSettings.Password;
    private readonly string _fromAddress = smtpSettings.From;
    private readonly string _toAddress = smtpSettings.To;
    private readonly bool _enableSsl = smtpSettings.EnableSsl;

    /// <inheritdoc />
    public async Task SendAsync(string to, string subject, string body, bool isHtml = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(to);
        ArgumentException.ThrowIfNullOrEmpty(subject);
        ArgumentException.ThrowIfNullOrEmpty(body);

        using var smtpClient = new SmtpClient(_host, _port)
        {
            Credentials = new NetworkCredential(_username, _password),
            EnableSsl = _enableSsl
        };

        using var message = new MailMessage(_fromAddress, to, subject, body)
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
        => SendAsync(_toAddress, subject, body, isHtml, cancellationToken);
}

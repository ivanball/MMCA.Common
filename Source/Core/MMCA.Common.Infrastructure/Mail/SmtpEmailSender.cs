using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure.Mail;

namespace MMCA.Common.Infrastructure.Mail;

/// <summary>
/// Sends emails via SMTP using settings from <see cref="SmtpSettings"/>.
/// Each call creates a new <see cref="SmtpClient"/> and disposes it after sending.
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly SmtpSettings _smtpSettings;
    private readonly bool _enableSsl;

    /// <summary>
    /// Initializes the sender from settings alone, taking <see cref="SmtpSettings.EnableSsl"/>
    /// exactly as configured.
    /// </summary>
    /// <param name="smtpOptions">The bound SMTP settings.</param>
    /// <remarks>
    /// Kept for a caller that constructs the sender by hand. The container prefers the overload
    /// below, which resolves TLS securely by default (SEC-Common-54).
    /// </remarks>
    public SmtpEmailSender(IOptions<SmtpSettings> smtpOptions)
    {
        ArgumentNullException.ThrowIfNull(smtpOptions);
        _smtpSettings = smtpOptions.Value;
        _enableSsl = _smtpSettings.EnableSsl;
    }

    /// <summary>
    /// Initializes the sender and resolves TLS through <see cref="SmtpTransportSecurity"/>:
    /// explicit <c>Smtp:EnableSsl</c> setting, otherwise on outside Development (SEC-Common-54).
    /// </summary>
    /// <param name="smtpOptions">The bound SMTP settings.</param>
    /// <param name="configuration">Application configuration, read for the explicit setting.</param>
    /// <param name="environment">The host environment, deciding the default.</param>
    /// <param name="logger">Logger warning once when a deployed host disables TLS deliberately.</param>
    /// <remarks>
    /// A second constructor rather than an optional parameter, the same pattern
    /// <c>EntityQueryService</c> uses: the container has no notion of an optional dependency, and
    /// removing the original constructor would be a breaking public-API change.
    /// </remarks>
    public SmtpEmailSender(
        IOptions<SmtpSettings> smtpOptions,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<SmtpEmailSender> logger)
    {
        ArgumentNullException.ThrowIfNull(smtpOptions);
        _smtpSettings = smtpOptions.Value;
        _enableSsl = SmtpTransportSecurity.Resolve(_smtpSettings, configuration, environment, logger);
    }

    /// <inheritdoc />
    public async Task SendAsync(string to, string subject, string body, bool isHtml = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(to);
        ArgumentException.ThrowIfNullOrEmpty(subject);
        ArgumentException.ThrowIfNullOrEmpty(body);

        using var smtpClient = CreateClient(_smtpSettings, _enableSsl);

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

    /// <summary>
    /// Builds the per-send client. Factored out so the bounded timeout is assertable without a live
    /// relay: the framework's own guarantee here is that <see cref="SmtpSettings.TimeoutSeconds"/>
    /// reaches <see cref="SmtpClient.Timeout"/>, and an end-to-end test of that would have to stall a
    /// real socket for the duration.
    /// </summary>
    /// <param name="settings">The bound SMTP settings.</param>
    /// <param name="enableSsl">The resolved TLS decision (see <see cref="SmtpTransportSecurity"/>).</param>
    /// <returns>A configured client the caller owns and disposes.</returns>
    internal static SmtpClient CreateClient(SmtpSettings settings, bool enableSsl) =>
#pragma warning disable S5332 // TLS is resolved by SmtpTransportSecurity: on outside Development, off only when the host set Smtp:EnableSsl=false (local dev targets MailDev, which offers no TLS)
        new(settings.Host, settings.Port)
        {
            Credentials = new NetworkCredential(settings.Username, settings.Password),
            EnableSsl = enableSsl,

            // SmtpClient's own default is 100 seconds, longer than the callers that sit in front of
            // it: a relay that accepts the connection and never answers would hold the request for
            // the whole of it. Bounded explicitly, and validated at startup (ADR-070).
            Timeout = (int)TimeSpan.FromSeconds(settings.TimeoutSeconds).TotalMilliseconds,
        };
#pragma warning restore S5332
}

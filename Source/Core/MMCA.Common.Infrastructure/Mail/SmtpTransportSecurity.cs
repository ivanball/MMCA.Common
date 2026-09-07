using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.Infrastructure.Mail;

/// <summary>
/// Resolves whether the SMTP client negotiates TLS, in the same three steps
/// <c>AddForwardedJwtBearer</c> resolves <c>RequireHttpsMetadata</c>: explicit setting, then
/// configuration, then secure outside Development.
/// </summary>
/// <remarks>
/// <para>
/// SEC-Common-54. <see cref="SmtpSettings.EnableSsl"/> is a plain <see langword="bool"/>, so an
/// adopter who configured <c>Smtp:Host</c>, <c>Username</c> and <c>Password</c> for a hosted relay
/// and simply omitted <c>EnableSsl</c> got a cleartext, authenticated SMTP session: an on-path
/// observer captured the relay credentials and every message body, including the single-use
/// password-reset token and link the framework's own forgot-password handler sends. Nothing
/// validated or warned.
/// </para>
/// <para>
/// The default is now TLS outside Development. Local development still targets MailDev, which offers
/// no TLS, so Development keeps the old default and a Development host needs no configuration.
/// </para>
/// </remarks>
public static partial class SmtpTransportSecurity
{
    /// <summary>Configuration key that pins the answer explicitly.</summary>
    public const string EnableSslConfigKey = "Smtp:EnableSsl";

    /// <summary>
    /// Resolves the effective value.
    /// </summary>
    /// <param name="configuration">Application configuration, or null.</param>
    /// <param name="environment">The host environment, or null (treated as non-Development).</param>
    /// <returns><see langword="true"/> when the SMTP session must negotiate TLS.</returns>
    public static bool Resolve(IConfiguration? configuration, IHostEnvironment? environment)
    {
        // Step 1 and 2 are one read: the settings object cannot distinguish "configured false" from
        // "never configured", so the presence of the KEY is what the decision turns on.
        var configured = configuration?[EnableSslConfigKey];
        if (!string.IsNullOrWhiteSpace(configured) && bool.TryParse(configured, out var explicitValue))
        {
            return explicitValue;
        }

        // Step 3: secure unless the host says it is Development.
        return environment?.IsDevelopment() != true;
    }

    /// <summary>
    /// Resolves the effective value and logs one startup warning when a non-Development host has
    /// deliberately turned TLS off for a configured relay, so the decision is visible in the logs of
    /// the deployment that made it.
    /// </summary>
    /// <param name="settings">The bound SMTP settings.</param>
    /// <param name="configuration">Application configuration, or null.</param>
    /// <param name="environment">The host environment, or null.</param>
    /// <param name="logger">Logger for the warning, or null to skip it.</param>
    /// <returns><see langword="true"/> when the SMTP session must negotiate TLS.</returns>
    public static bool Resolve(
        SmtpSettings settings,
        IConfiguration? configuration,
        IHostEnvironment? environment,
        ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var enableSsl = Resolve(configuration, environment);

        if (!enableSsl
            && environment?.IsDevelopment() != true
            && !string.IsNullOrWhiteSpace(settings.Host)
            && logger is not null)
        {
            LogCleartextSmtp(logger, EnableSslConfigKey, settings.Host);
        }

        return enableSsl;
    }

    [LoggerMessage(
        EventId = 5401,
        Level = LogLevel.Warning,
        Message = "SMTP TLS is disabled outside Development by '{ConfigKey}': credentials and message bodies to '{SmtpHost}' (password-reset tokens included) travel in cleartext")]
    private static partial void LogCleartextSmtp(ILogger logger, string configKey, string smtpHost);
}

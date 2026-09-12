using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.Infrastructure.Mail;

/// <summary>
/// SMTP mail server settings bound from the <c>Smtp</c> configuration section.
/// Validated via data annotations on startup.
/// </summary>
public sealed class SmtpSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "Smtp";

    /// <summary>Standard SMTP port used as the default when none is configured.</summary>
    public static readonly int DefaultSmtpPort = 25;

    /// <summary>Gets the SMTP server hostname.</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>Gets the SMTP server port (1-65535).</summary>
    [Range(1, 65535)]
    public int Port { get; init; } = DefaultSmtpPort;

    /// <summary>Gets the SMTP authentication username.</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>Gets the SMTP authentication password.</summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether SSL/TLS is enabled for the SMTP connection.
    /// <para>
    /// Leaving <c>Smtp:EnableSsl</c> unset no longer means "off": the framework resolves it through
    /// <see cref="SmtpTransportSecurity"/>, which turns TLS ON outside Development (SEC-Common-54).
    /// Set the key explicitly to <see langword="false"/> only for a relay that genuinely offers no
    /// TLS, and expect one startup warning naming the key.
    /// </para>
    /// </summary>
    public bool EnableSsl { get; init; }

    /// <summary>Gets the default sender email address.</summary>
    public string From { get; init; } = string.Empty;

    /// <summary>Gets the default recipient email address (used by the no-argument <c>SendAsync</c> overload).</summary>
    public string To { get; init; } = string.Empty;

    /// <summary>
    /// Gets how long a single send may take before it is abandoned, in seconds (1-600, default 30).
    /// <para>
    /// The .NET default is 100 seconds, which is longer than most callers' own budget: a relay that
    /// accepts the TCP connection and then stops answering (SEC-Common: a hung or throttling
    /// provider) parks the request thread for the full 100s, and a notification burst parks one per
    /// message. 30 seconds keeps a stalled relay inside the caller's timeout instead of outliving
    /// it. The range is validated at startup (ADR-070 fail-fast configuration), so a zero or a typo
    /// is a startup failure rather than either an instant abort or an unbounded wait.
    /// </para>
    /// </summary>
    [Range(1, 600)]
    public int TimeoutSeconds { get; init; } = 30;
}

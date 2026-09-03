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

    /// <summary>Gets a value indicating whether SSL/TLS is enabled for the SMTP connection.</summary>
    public bool EnableSsl { get; init; }

    /// <summary>Gets the default sender email address.</summary>
    public string From { get; init; } = string.Empty;

    /// <summary>Gets the default recipient email address (used by the no-argument <c>SendAsync</c> overload).</summary>
    public string To { get; init; } = string.Empty;
}

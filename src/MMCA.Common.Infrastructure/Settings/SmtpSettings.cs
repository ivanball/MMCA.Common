using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.Infrastructure.Settings;

/// <summary>
/// Concrete SMTP settings bound from the <c>Smtp</c> configuration section.
/// Validated via data annotations on startup.
/// </summary>
public sealed class SmtpSettings : ISmtpSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "Smtp";

    /// <summary>Standard SMTP port used as the default when none is configured.</summary>
    public static readonly int DefaultSmtpPort = 25;

    /// <inheritdoc />
    public string Host { get; init; } = string.Empty;

    /// <inheritdoc />
    [Range(1, 65535)]
    public int Port { get; init; } = DefaultSmtpPort;

    /// <inheritdoc />
    public string Username { get; init; } = string.Empty;

    /// <inheritdoc />
    public string Password { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool EnableSsl { get; init; }

    /// <inheritdoc />
    public string From { get; init; } = string.Empty;

    /// <inheritdoc />
    public string To { get; init; } = string.Empty;
}

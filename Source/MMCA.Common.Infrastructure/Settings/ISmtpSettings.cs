namespace MMCA.Common.Infrastructure.Settings;

/// <summary>
/// SMTP mail server configuration. Bound from the <c>Smtp</c> configuration section.
/// </summary>
public interface ISmtpSettings
{
    /// <summary>Gets the SMTP server hostname.</summary>
    string Host { get; init; }

    /// <summary>Gets the SMTP server port (1-65535).</summary>
    int Port { get; init; }

    /// <summary>Gets the SMTP authentication username.</summary>
    string Username { get; init; }

    /// <summary>Gets the SMTP authentication password.</summary>
    string Password { get; init; }

    /// <summary>Gets a value indicating whether SSL/TLS is enabled for the SMTP connection.</summary>
    bool EnableSsl { get; init; }

    /// <summary>Gets the default sender email address.</summary>
    string From { get; init; }

    /// <summary>Gets the default recipient email address (used by the no-argument <c>SendAsync</c> overload).</summary>
    string To { get; init; }
}

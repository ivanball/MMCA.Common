using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.Infrastructure.Auth;

/// <summary>
/// Configuration settings for login brute-force protection and registration rate limiting.
/// Bound from the <c>LoginProtection</c> configuration section.
/// </summary>
public sealed class LoginProtectionSettings
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "LoginProtection";

    /// <summary>
    /// Maximum number of failed login attempts before exponential lockout is applied.
    /// </summary>
    [Range(1, 100)]
    public int MaxFailedAttempts { get; init; } = 5;

    /// <summary>
    /// Maximum lockout duration in seconds. Exponential backoff is capped at this value.
    /// </summary>
    [Range(1, 3600)]
    public int MaxLockoutSeconds { get; init; } = 300;

    /// <summary>
    /// Time window in minutes during which failed attempts are counted. After this window
    /// expires, the attempt counter resets automatically via cache expiration.
    /// </summary>
    [Range(1, 1440)]
    public int FailedAttemptWindowMinutes { get; init; } = 30;

    /// <summary>
    /// Maximum number of user registrations allowed per IP address within the rate limit window.
    /// </summary>
    [Range(1, 10000)]
    public int MaxRegistrationsPerIpPerHour { get; init; } = 10;

    /// <summary>
    /// Time window in minutes for the registration rate limit. Defaults to 60 minutes (1 hour).
    /// </summary>
    [Range(1, 1440)]
    public int RegistrationRateLimitWindowMinutes { get; init; } = 60;
}

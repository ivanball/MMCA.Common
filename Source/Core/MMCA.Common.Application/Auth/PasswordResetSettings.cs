using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace MMCA.Common.Application.Auth;

/// <summary>
/// Configuration settings for the forgot-password workflow. Bound from the <c>PasswordReset</c>
/// configuration section.
/// </summary>
public sealed class PasswordResetSettings
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "PasswordReset";

    /// <summary>
    /// Absolute URL of the reset page the email links to (the email appends
    /// <c>?email=...&amp;token=...</c>). Deliberately NOT required: a host that has not configured a
    /// UI base must still boot, and an empty value degrades to a token-only email the user pastes
    /// into the reset page by hand.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1056:URI-like properties should not be strings",
        Justification = "Bound from configuration (PasswordReset__ResetUrl) and concatenated with a query string; the empty default that keeps an unconfigured host bootable is not a valid System.Uri.")]
    public string ResetUrl { get; init; } = string.Empty;

    /// <summary>How long an issued token stays redeemable, in minutes.</summary>
    [Range(1, 1440)]
    public int TokenLifetimeMinutes { get; init; } = 30;

    /// <summary>
    /// Number of wrong tokens tolerated for one issued token before the record is discarded and the
    /// user has to request a new one.
    /// </summary>
    [Range(1, 100)]
    public int MaxValidationAttempts { get; init; } = 5;

    /// <summary>Maximum reset requests accepted per email address within the request window.</summary>
    [Range(1, 100)]
    public int MaxRequestsPerEmail { get; init; } = 3;

    /// <summary>Length of the per-email request-throttle window, in minutes.</summary>
    [Range(1, 1440)]
    public int RequestWindowMinutes { get; init; } = 60;
}

using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace MMCA.Common.Application.Auth.EmailConfirmation;

/// <summary>
/// Configuration for the email-confirmation workflow. Bound from the
/// <c>Authentication:EmailConfirmation</c> configuration section.
/// </summary>
public sealed class EmailConfirmationSettings
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Authentication:EmailConfirmation";

    /// <summary>
    /// Absolute URL of the page the confirmation email links to (the email appends the address and
    /// the token). Deliberately NOT required, matching <c>PasswordResetSettings.ResetUrl</c>: a host
    /// that has not configured a UI base must still boot, and an empty value degrades to a
    /// token-only email the user pastes into the confirmation page by hand.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1056:URI-like properties should not be strings",
        Justification = "Bound from configuration and concatenated with a fragment; the empty default that keeps an unconfigured host bootable is not a valid System.Uri.")]
    public string ConfirmationUrl { get; init; } = string.Empty;

    /// <summary>How long an issued confirmation token stays redeemable, in minutes. Defaults to one day.</summary>
    [Range(1, 43200)]
    public int TokenLifetimeMinutes { get; init; } = 1440;

    /// <summary>
    /// Number of wrong tokens tolerated for one issued token before the record is discarded and the
    /// user has to request a new email.
    /// </summary>
    [Range(1, 100)]
    public int MaxValidationAttempts { get; init; } = 5;

    /// <summary>Maximum confirmation emails accepted per address within the request window.</summary>
    [Range(1, 100)]
    public int MaxRequestsPerEmail { get; init; } = 3;

    /// <summary>Length of the per-address request-throttle window, in minutes.</summary>
    [Range(1, 1440)]
    public int RequestWindowMinutes { get; init; } = 60;

    /// <summary>
    /// Whether an account with an unconfirmed address is refused at sign-in.
    /// </summary>
    /// <remarks>
    /// <b>Default false, and that default is load-bearing.</b> Turning it on locks out every existing
    /// account whose address was never confirmed, so a host adopting confirmation backfills its
    /// existing rows as confirmed first and flips this afterwards. With it off, the whole feature is
    /// additive: tokens are issued and redeemed, and nothing about sign-in changes.
    /// </remarks>
    public bool RequireConfirmedEmail { get; init; }
}

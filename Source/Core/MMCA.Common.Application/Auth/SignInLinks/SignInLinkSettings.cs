using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace MMCA.Common.Application.Auth.SignInLinks;

/// <summary>
/// Configuration for sign-in links (ADR-116). Bound from the <c>Authentication:SignInLinks</c>
/// configuration section.
/// </summary>
public sealed class SignInLinkSettings
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Authentication:SignInLinks";

    /// <summary>
    /// Whether sign-in links are issued and redeemed at all. Default false: registering the
    /// capability changes nothing until a host turns it on, and turning it off again makes every
    /// outstanding link inert without waiting for it to expire.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Absolute URL of the page the emailed link opens (the link appends the address and the token in
    /// the fragment). Deliberately not required, matching <c>EmailConfirmationSettings.ConfirmationUrl</c>
    /// so an unconfigured host still boots; with it empty no link can be composed and nothing is sent.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1056:URI-like properties should not be strings",
        Justification = "Bound from configuration and concatenated with a fragment; the empty default that keeps an unconfigured host bootable is not a valid System.Uri.")]
    public string SignInUrl { get; init; } = string.Empty;

    /// <summary>How long a link the account owner asked for stays redeemable, in minutes. Defaults to 15.</summary>
    [Range(1, 1440)]
    public int SignInLifetimeMinutes { get; init; } = 15;

    /// <summary>How long an invitation link stays redeemable, in minutes. Defaults to 14 days.</summary>
    [Range(1, 43200)]
    public int InviteLifetimeMinutes { get; init; } = 20160;

    /// <summary>
    /// Number of wrong tokens tolerated for one address before its outstanding tokens are discarded.
    /// </summary>
    [Range(1, 100)]
    public int MaxValidationAttempts { get; init; } = 5;

    /// <summary>Maximum sign-in link emails accepted per address within the request window.</summary>
    [Range(1, 100)]
    public int MaxRequestsPerEmail { get; init; } = 3;

    /// <summary>Length of the per-address request-throttle window, in minutes.</summary>
    [Range(1, 1440)]
    public int RequestWindowMinutes { get; init; } = 60;

    /// <summary>The lifetime of a token issued for <paramref name="purpose"/>.</summary>
    /// <param name="purpose">Why the link is sent.</param>
    /// <returns>The lifetime.</returns>
    public TimeSpan LifetimeFor(SignInLinkPurpose purpose) => TimeSpan.FromMinutes(
        purpose == SignInLinkPurpose.Invite ? InviteLifetimeMinutes : SignInLifetimeMinutes);

    /// <summary>
    /// Builds the link to email, or returns <see langword="null"/> when <see cref="SignInUrl"/> is
    /// unconfigured.
    /// </summary>
    /// <param name="email">The normalized address the token was issued for.</param>
    /// <param name="token">The raw single-use token.</param>
    /// <returns>The link, or <see langword="null"/>.</returns>
    /// <remarks>
    /// SECURITY: the address and the token ride in the URL FRAGMENT, for the reason documented on
    /// <c>ForgotPasswordHandlerBase.ComposeResetLink</c>: a fragment is never sent to a server, so the
    /// live token stays out of access logs, request telemetry and <c>Referer</c> headers, and a mail
    /// scanner that prefetches the link with a GET never sees, and so never spends, the token.
    /// </remarks>
    public string? ComposeLink(string email, string token) =>
        string.IsNullOrWhiteSpace(SignInUrl)
            ? null
            : $"{SignInUrl}#email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
}

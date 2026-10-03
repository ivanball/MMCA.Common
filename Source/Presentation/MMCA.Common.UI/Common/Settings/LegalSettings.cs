using System.Diagnostics.CodeAnalysis;

namespace MMCA.Common.UI.Common.Settings;

/// <summary>
/// Strongly-typed options bound to the <c>"Legal"</c> configuration section: where the host's legal
/// documents live. Every URL is optional and absolute (the documents are usually published outside
/// the app); an empty value renders nothing for that document, so a host that configures none of
/// them sees no footer links and no registration checkbox.
/// </summary>
/// <remarks>
/// <see cref="TermsUrl"/> is the switch for the registration checkbox: with it set, the register page
/// requires "I agree" before it submits. The current terms version itself is server configuration
/// (<c>Legal:CurrentTermsVersion</c> on the API host), never read by the UI.
/// </remarks>
public sealed class LegalSettings
{
    private const string UrlJustification =
        "Bound from configuration and emitted straight into an href; the empty default that means 'not configured' is not a valid System.Uri (the LayoutSettings.BrandLogoUrl precedent).";

    /// <summary>Configuration section name used for binding.</summary>
    public static readonly string SectionName = "Legal";

    /// <summary>Absolute URL of the Terms of Service. Empty (the default) hides the link and the registration checkbox.</summary>
    [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = UrlJustification)]
    public string TermsUrl { get; init; } = string.Empty;

    /// <summary>Absolute URL of the Privacy Policy. Empty (the default) hides the link.</summary>
    [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = UrlJustification)]
    public string PrivacyUrl { get; init; } = string.Empty;

    /// <summary>Absolute URL of the Code of Conduct. Empty (the default) hides the link.</summary>
    [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = UrlJustification)]
    public string CodeOfConductUrl { get; init; } = string.Empty;

    /// <summary>
    /// Absolute URL of the page that explains how to delete an account (an app-store requirement for
    /// apps that offer sign-up). Empty (the default) when not published.
    /// </summary>
    [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = UrlJustification)]
    public string DeleteAccountUrl { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether any of the three footer documents is configured.</summary>
    public bool HasFooterLinks =>
        !string.IsNullOrWhiteSpace(TermsUrl)
        || !string.IsNullOrWhiteSpace(PrivacyUrl)
        || !string.IsNullOrWhiteSpace(CodeOfConductUrl);
}

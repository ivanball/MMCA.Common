namespace MMCA.Common.UI.Common.Settings;

/// <summary>
/// Strongly-typed options bound to the <c>"SignIn"</c> configuration section: which optional actions
/// the shared sign-in pages offer. Every option defaults to the pages' long-standing behaviour, so a
/// host that configures nothing sees no change.
/// </summary>
/// <remarks>
/// A WebAssembly client reads configuration from <c>client-config</c>, so a host that turns an option
/// on there also adds this section to the document it serves.
/// </remarks>
public sealed class SignInSettings
{
    /// <summary>Configuration section name used for binding.</summary>
    public static readonly string SectionName = "SignIn";

    /// <summary>
    /// Gets or sets a value indicating whether the sign-in page offers "Email me a sign-in link"
    /// (ADR-116). Default <see langword="false"/>; turn it on only alongside the server's
    /// <c>Authentication:SignInLinks:Enabled</c>, or the button sends requests the API ignores.
    /// </summary>
    public bool OfferSignInLink { get; set; }

    /// <summary>
    /// Gets or sets the local path a redeemed sign-in link lands on. Defaults to the home page; an
    /// absolute or off-site value is ignored in favour of the default.
    /// </summary>
    public string SignInLinkLandingPath { get; set; } = "/";
}

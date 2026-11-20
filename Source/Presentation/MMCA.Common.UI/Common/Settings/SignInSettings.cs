namespace MMCA.Common.UI.Common.Settings;

/// <summary>
/// Strongly-typed options bound to the <c>"SignIn"</c> configuration section: which optional actions
/// the shared sign-in pages offer. Every option defaults to the pages' long-standing behaviour, so a
/// host that configures nothing sees no change.
/// </summary>
/// <remarks>
/// A WebAssembly client reads configuration from <c>client-config</c>, which forwards the server
/// head's <c>SignIn</c> section (as it does <c>Layout</c>, <c>Registration</c> and <c>Legal</c>), so an
/// option turned on in the server configuration reaches the client with no second copy.
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
    /// Gets or sets a value indicating whether the sign-in page offers "Sign in with a passkey" and
    /// passkey autofill on the email field (ADR-139). Default <see langword="false"/>; turn it on only
    /// alongside the server's <c>Passkeys:Enabled</c>. Shown only where the head can run WebAuthn
    /// (<c>IPasskeyCapability.IsAvailable</c>), so a MAUI head never shows it.
    /// </summary>
    public bool OfferPasskeys { get; set; }

    /// <summary>
    /// Gets or sets the local path a redeemed sign-in link lands on. Defaults to the home page; an
    /// absolute or off-site value is ignored in favour of the default.
    /// </summary>
    public string SignInLinkLandingPath { get; set; } = "/";
}

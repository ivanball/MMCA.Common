using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace MMCA.Common.Aspire.Hosting;

/// <summary>
/// AppHost helpers that hand the Identity service the addresses it cannot know by itself: the UI
/// links it puts in emails and OAuth redirects, and the JWT issuer.
/// <para>
/// This package does not reference the packages that own those settings, so the section and
/// property names below are internal MIRRORS of names owned by <c>PasswordResetSettings</c>,
/// <c>EmailConfirmationSettings</c>, <c>SignInLinkSettings</c>, <c>JwtSettings</c> and
/// <c>OAuthControllerBase.UIBaseUrlConfigKey</c>; the hosting tests reference the owners and
/// cross-assert every mirror, so a rename there fails a test instead of orphaning the wiring.
/// </para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with extension(T) blocks, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class IdentityLinkExtensions
{
    /// <summary>Mirror of the section of <c>OAuthControllerBase.UIBaseUrlConfigKey</c>.</summary>
    internal const string OAuthSection = "OAuth";

    /// <summary>Mirror of the property of <c>OAuthControllerBase.UIBaseUrlConfigKey</c>.</summary>
    internal const string OAuthUiBaseUrlProperty = "UIBaseUrl";

    /// <summary>Mirror of <c>PasswordResetSettings.SectionName</c>.</summary>
    internal const string PasswordResetSection = "PasswordReset";

    /// <summary>Mirror of <c>EmailConfirmationSettings.SectionName</c>.</summary>
    internal const string EmailConfirmationSection = "Authentication:EmailConfirmation";

    /// <summary>Mirror of <c>SignInLinkSettings.SectionName</c>.</summary>
    internal const string SignInLinksSection = "Authentication:SignInLinks";

    /// <summary>Mirror of <c>JwtSettings.SectionName</c>.</summary>
    internal const string JwtSection = "Jwt";

    /// <summary>Route of the reset-password page in MMCA.Common.UI.</summary>
    internal const string ResetPasswordPath = "/reset-password";

    /// <summary>Route of the confirm-email page in MMCA.Common.UI.</summary>
    internal const string ConfirmEmailPath = "/confirm-email";

    /// <summary>Route of the sign-in-link page in MMCA.Common.UI.</summary>
    internal const string SignInLinkPath = "/auth/sign-in-link";

    private const string HttpsEndpointName = "https";

    /// <summary>
    /// Maps a configuration section and property to the environment-variable form .NET configuration
    /// binds: every <c>:</c> becomes <c>__</c>, and the property is appended after <c>__</c>.
    /// </summary>
    /// <param name="section">The configuration section (for example <c>Authentication:SignInLinks</c>).</param>
    /// <param name="property">The settings property name.</param>
    /// <returns>The environment variable name.</returns>
    internal static string EnvironmentKey(string section, string property) =>
        section.Replace(":", "__", StringComparison.Ordinal) + "__" + property;

    extension(IResourceBuilder<ProjectResource> identity)
    {
        /// <summary>
        /// Points the four UI links the Identity service emits at the UI's https endpoint: the OAuth
        /// redirect base (<c>OAuth__UIBaseUrl</c>, the bare endpoint), the password-reset link, the
        /// email-confirmation link and the sign-in link (each the endpoint plus the page route). Adds
        /// no reference and no wait.
        /// </summary>
        /// <param name="ui">The UI project resource whose <c>https</c> endpoint roots the links.</param>
        /// <returns>The same Identity resource builder, for chaining.</returns>
        public IResourceBuilder<ProjectResource> WithUiLinks(IResourceBuilder<ProjectResource> ui)
        {
            ArgumentNullException.ThrowIfNull(identity);
            ArgumentNullException.ThrowIfNull(ui);

            var uiHttps = ui.GetEndpoint(HttpsEndpointName);

            return identity
                .WithEnvironment(EnvironmentKey(OAuthSection, OAuthUiBaseUrlProperty), uiHttps)
                .WithEnvironment(
                    EnvironmentKey(PasswordResetSection, "ResetUrl"),
                    ReferenceExpression.Create($"{uiHttps}{ResetPasswordPath}"))
                .WithEnvironment(
                    EnvironmentKey(EmailConfirmationSection, "ConfirmationUrl"),
                    ReferenceExpression.Create($"{uiHttps}{ConfirmEmailPath}"))
                .WithEnvironment(
                    EnvironmentKey(SignInLinksSection, "SignInUrl"),
                    ReferenceExpression.Create($"{uiHttps}{SignInLinkPath}"));
        }

        /// <summary>
        /// Sets the Identity service's JWT issuer (<c>Jwt__Issuer</c>) to the gateway's https
        /// endpoint, the address clients see. Adds no reference and no wait.
        /// </summary>
        /// <param name="gateway">The gateway project resource whose <c>https</c> endpoint is the issuer.</param>
        /// <returns>The same Identity resource builder, for chaining.</returns>
        public IResourceBuilder<ProjectResource> WithIssuer(IResourceBuilder<ProjectResource> gateway)
        {
            ArgumentNullException.ThrowIfNull(identity);
            ArgumentNullException.ThrowIfNull(gateway);

            return identity.WithEnvironment(EnvironmentKey(JwtSection, "Issuer"), gateway.GetEndpoint(HttpsEndpointName));
        }
    }
}

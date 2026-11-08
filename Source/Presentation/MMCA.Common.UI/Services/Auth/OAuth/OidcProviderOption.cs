namespace MMCA.Common.UI.Services.Auth.OAuth;

/// <summary>
/// One configured OIDC sign-in provider as the sign-in page sees it (never its client id or secret).
/// </summary>
/// <param name="Name">The provider name: its scheme name and the <c>/auth/oauth/{name}</c> route segment.</param>
/// <param name="DisplayName">The name on the provider's button.</param>
/// <param name="UsesDomains">
/// Whether the provider declares email domains, which turns on the email-first
/// "Sign in with your organization" step.
/// </param>
public sealed record OidcProviderOption(string Name, string DisplayName, bool UsesDomains);

using AspNet.Security.OAuth.GitHub;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.API.Authentication;

/// <summary>
/// Registers the external OAuth provider schemes (Google, GitHub) and the temporary
/// <see cref="ExternalLoginScheme"/> cookie that the app's <c>OAuthController</c>
/// (subclassing <see cref="Controllers.OAuthControllerBase"/>) consumes.
/// <para>
/// <c>AddCommonAuthentication</c> only registers the JWT bearer scheme, so without this call the
/// OAuth controller would <c>Challenge</c> and read schemes that are not in the authentication
/// pipeline and the flow fails at runtime. Registration is conditional on
/// <c>OAuth:&lt;Provider&gt;:ClientId</c> being present — the same signal the UI's
/// <c>ConfigurationOAuthUISettings</c> uses for <c>GoogleEnabled</c>/<c>GitHubEnabled</c> — so a
/// host with no OAuth configuration keeps the JWT-only default untouched (inert until configured,
/// the same posture as <c>AddPermissions</c>, ADR-020).
/// </para>
/// </summary>
public static class ExternalAuthExtensions
{
    /// <summary>
    /// Name of the cookie scheme the OAuth handlers sign into between the provider callback and
    /// the OAuth controller's <c>CompleteAsync</c>. Shared with the controller so the two never drift.
    /// </summary>
    public const string ExternalLoginScheme = "ExternalLogin";

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Adds the <see cref="ExternalLoginScheme"/> cookie plus the Google and/or GitHub OAuth
        /// schemes, each gated on its <c>OAuth:&lt;Provider&gt;:ClientId</c> being configured.
        /// </summary>
        /// <param name="configuration">Application configuration (reads the <c>OAuth</c> section).</param>
        /// <returns>The <see cref="IServiceCollection"/> for chaining.</returns>
        public IServiceCollection AddExternalAuthProviders(IConfiguration configuration)
        {
            var oauth = configuration.GetSection("OAuth");
            var googleClientId = oauth["Google:ClientId"];
            var githubClientId = oauth["GitHub:ClientId"];

            var googleEnabled = !string.IsNullOrEmpty(googleClientId);
            var githubEnabled = !string.IsNullOrEmpty(githubClientId);

            // No provider configured → leave the JWT-only pipeline exactly as AddCommonAuthentication
            // left it. This keeps environments without OAuth secrets (most tests, local dev) unchanged.
            if (!googleEnabled && !githubEnabled)
            {
                return services;
            }

            // AddAuthentication() with no argument does not reset the default scheme set by
            // AddCommonAuthentication (JwtBearer); it just yields a builder to append schemes onto.
            var authBuilder = services.AddAuthentication();

            // Short-lived cookie carrying the external principal from the provider callback to
            // the controller's CompleteAsync, which signs it out as soon as the local JWT pair is minted.
            authBuilder.AddCookie(ExternalLoginScheme, options =>
            {
                options.Cookie.Name = "mmca_external_login";
                options.Cookie.HttpOnly = true;
                // The OAuth round trip returns as a top-level GET navigation, so Lax is sufficient
                // and avoids the Secure+cross-site requirement that SameSite=None imposes.
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
            });

            if (googleEnabled)
            {
                authBuilder.AddGoogle(options =>
                {
                    options.ClientId = googleClientId!;
                    options.ClientSecret = oauth["Google:ClientSecret"]
                        ?? throw new InvalidOperationException(
                            "OAuth:Google:ClientSecret is required when OAuth:Google:ClientId is set.");
                    options.SignInScheme = ExternalLoginScheme;
                    options.CallbackPath = "/auth/callback/google";
                    options.SaveTokens = true;
                });
            }

            if (githubEnabled)
            {
                authBuilder.AddGitHub(options =>
                {
                    options.ClientId = githubClientId!;
                    options.ClientSecret = oauth["GitHub:ClientSecret"]
                        ?? throw new InvalidOperationException(
                            "OAuth:GitHub:ClientSecret is required when OAuth:GitHub:ClientId is set.");
                    options.SignInScheme = ExternalLoginScheme;
                    options.CallbackPath = "/auth/callback/github";
                    // GitHub does not return the email on the default scope; request it so the
                    // ClaimTypes.Email lookup in OAuthControllerBase.ExtractClaims succeeds.
                    options.Scope.Add("user:email");
                    options.SaveTokens = true;
                });
            }

            return services;
        }
    }
}

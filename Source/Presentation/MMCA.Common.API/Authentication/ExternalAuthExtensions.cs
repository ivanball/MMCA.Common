using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace MMCA.Common.API.Authentication;

/// <summary>
/// Registers the external OAuth provider schemes (Google, GitHub, Apple) and the temporary
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
/// <para>
/// Any OIDC provider (Entra ID, Okta, ...) joins by configuration: each <c>OAuth:Oidc:&lt;Name&gt;</c>
/// section with a <c>ClientId</c> (plus <c>Authority</c>, <c>ClientSecret</c>, and optionally
/// <c>Scopes</c>, <c>DisplayName</c>, <c>Domains</c>, <c>TrustEmail</c>) becomes an OpenID Connect
/// scheme named <c>&lt;Name&gt;</c>, challenged at <c>auth/oauth/&lt;Name&gt;</c> with its callback at
/// <c>/auth/callback/&lt;Name&gt;</c>. The names google, github and apple are reserved.
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
        /// Adds the <see cref="ExternalLoginScheme"/> cookie plus the Google, GitHub, and/or Apple
        /// OAuth schemes and one OpenID Connect scheme per <c>OAuth:Oidc:&lt;Name&gt;</c> section, each
        /// gated on its <c>ClientId</c> being configured.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// An <c>OAuth:Oidc</c> section is misconfigured; the message names the key.
        /// </exception>
        /// <param name="configuration">Application configuration (reads the <c>OAuth</c> section).</param>
        /// <returns>The <see cref="IServiceCollection"/> for chaining.</returns>
        public IServiceCollection AddExternalAuthProviders(IConfiguration configuration)
        {
            var oauth = configuration.GetSection("OAuth");
            var googleClientId = oauth["Google:ClientId"];
            var githubClientId = oauth["GitHub:ClientId"];
            var appleClientId = oauth["Apple:ClientId"];

            // Read (and validated) eagerly, so a misconfigured section fails startup naming its key.
            var oidcProviders = OidcProviderSettings.ReadAll(configuration);

            // No provider configured → leave the JWT-only pipeline exactly as AddCommonAuthentication
            // left it. This keeps environments without OAuth secrets (most tests, local dev) unchanged.
            if (string.IsNullOrEmpty(googleClientId)
                && string.IsNullOrEmpty(githubClientId)
                && string.IsNullOrEmpty(appleClientId)
                && oidcProviders.Count == 0)
            {
                return services;
            }

            // AddAuthentication() with no argument does not reset the default scheme set by
            // AddCommonAuthentication (JwtBearer); it just yields a builder to append schemes onto.
            var authBuilder = services.AddAuthentication();

            AddExternalLoginCookie(authBuilder);

            if (!string.IsNullOrEmpty(googleClientId))
            {
                AddGoogleProvider(authBuilder, oauth, googleClientId);
            }

            if (!string.IsNullOrEmpty(githubClientId))
            {
                AddGitHubProvider(authBuilder, oauth, githubClientId);
            }

            if (!string.IsNullOrEmpty(appleClientId))
            {
                AddAppleProvider(authBuilder, oauth, appleClientId);
            }

            if (oidcProviders.Count > 0)
            {
                foreach (var oidcProvider in oidcProviders)
                {
                    AddOidcProvider(authBuilder, oidcProvider);
                }

                // The OAuth controller's generic challenge route and home-realm discovery read this.
                services.AddSingleton(new OidcProviderRegistry(oidcProviders));
            }

            return services;
        }
    }

    /// <summary>
    /// A generic OIDC provider (<c>OAuth:Oidc:&lt;Name&gt;</c>): authorization code flow with PKCE into
    /// the same <see cref="ExternalLoginScheme"/> cookie the built-in providers use. The handler keeps
    /// the ID token's raw claim names and <see cref="OidcClaimNormalizer"/> reshapes them for the
    /// controller once the token is validated.
    /// </summary>
    private static void AddOidcProvider(AuthenticationBuilder authBuilder, OidcProviderSettings provider) =>
        authBuilder.AddOpenIdConnect(provider.Name, provider.DisplayName, options =>
        {
            options.Authority = provider.Authority;
            options.ClientId = provider.ClientId;
            options.ClientSecret = provider.ClientSecret;
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.UsePkce = true;
            options.SignInScheme = ExternalLoginScheme;
            options.CallbackPath = $"/auth/callback/{provider.Name}";

            // Per-provider paths: the handler defaults are shared, so two OIDC providers would both
            // claim the same sign-out callback.
            options.SignedOutCallbackPath = $"/auth/signout-callback/{provider.Name}";
            options.RemoteSignOutPath = $"/auth/signout/{provider.Name}";

            // Raw claim names (sub, iss, tid, oid, email_verified) reach OnTokenValidated unmapped, and
            // the ID token stays out of the short-lived cookie: nothing downstream reads it.
            options.MapInboundClaims = false;
            options.SaveTokens = false;
            options.GetClaimsFromUserInfoEndpoint = false;

            options.Scope.Clear();
            foreach (var scope in provider.Scopes)
            {
                options.Scope.Add(scope);
            }

            options.Events.OnTokenValidated = context =>
            {
                context.Principal = OidcClaimNormalizer.Normalize(context.Principal!, provider.Name, provider.TrustEmail);
                return Task.CompletedTask;
            };
        });

    /// <summary>
    /// Short-lived cookie carrying the external principal from the provider callback to
    /// the controller's <c>CompleteAsync</c>, which signs it out as soon as the local JWT pair is minted.
    /// </summary>
    private static void AddExternalLoginCookie(AuthenticationBuilder authBuilder) =>
        authBuilder.AddCookie(ExternalLoginScheme, options =>
        {
            options.Cookie.Name = "mmca_external_login";
            options.Cookie.HttpOnly = true;
            // The OAuth round trip returns as a top-level GET navigation, so Lax is sufficient
            // and avoids the Secure+cross-site requirement that SameSite=None imposes.
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
        });

    private static void AddGoogleProvider(AuthenticationBuilder authBuilder, IConfigurationSection oauth, string clientId) =>
        authBuilder.AddGoogle(options =>
        {
            options.ClientId = clientId;
            options.ClientSecret = oauth["Google:ClientSecret"]
                ?? throw new InvalidOperationException(
                    "OAuth:Google:ClientSecret is required when OAuth:Google:ClientId is set.");
            options.SignInScheme = ExternalLoginScheme;
            options.CallbackPath = "/auth/callback/google";
            options.SaveTokens = true;
        });

    private static void AddGitHubProvider(AuthenticationBuilder authBuilder, IConfigurationSection oauth, string clientId) =>
        authBuilder.AddGitHub(options =>
        {
            options.ClientId = clientId;
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

    private static void AddAppleProvider(AuthenticationBuilder authBuilder, IConfigurationSection oauth, string clientId) =>
        authBuilder.AddApple(options =>
        {
            // ClientId is the Services ID (not the app bundle id) registered for
            // Sign in with Apple on the Apple Developer portal.
            options.ClientId = clientId;

            // Apple has no static client secret: the handler mints a short-lived ES256
            // JWT from the developer's private key (TeamId + KeyId identify the signer).
            options.GenerateClientSecret = true;
            options.TeamId = oauth["Apple:TeamId"]
                ?? throw new InvalidOperationException(
                    "OAuth:Apple:TeamId is required when OAuth:Apple:ClientId is set.");
            options.KeyId = oauth["Apple:KeyId"]
                ?? throw new InvalidOperationException(
                    "OAuth:Apple:KeyId is required when OAuth:Apple:ClientId is set.");
            var privateKeyPem = oauth["Apple:PrivateKeyPem"]
                ?? throw new InvalidOperationException(
                    "OAuth:Apple:PrivateKeyPem is required when OAuth:Apple:ClientId is set.");
            options.PrivateKey = (_, _) => Task.FromResult(privateKeyPem.AsMemory());

            options.SignInScheme = ExternalLoginScheme;
            options.CallbackPath = "/auth/callback/apple";
            options.SaveTokens = true;

            // The handler reads only the ID token, which never carries the name; Apple posts it once,
            // in the callback's "user" form field, on the first authorization (AppleUserNameClaims).
            options.Events.OnCreatingTicket = AppleUserNameClaims.AddFromCallbackFormAsync;
        });
}

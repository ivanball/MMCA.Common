using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.Startup.Auth;
using MMCA.Common.Infrastructure.Auth;

namespace MMCA.Common.API.Startup;

public static partial class WebApplicationBuilderExtensions
{
    /// <summary>
    /// Configuration key that overrides <c>AddForwardedJwtBearer</c>'s secure-by-default
    /// <c>RequireHttpsMetadata</c>. Set it to <see langword="false"/> only for a deployment whose
    /// authority is genuinely plain HTTP (an internal-ingress h2c service URL), and record the
    /// justification beside the setting in the deployment template.
    /// </summary>
    public const string RequireHttpsMetadataConfigKey = "Authentication:JwtBearer:RequireHttpsMetadata";

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers JWT Bearer authentication that trusts an external Identity service's JWKS
        /// endpoint via OIDC-style authority discovery. Use this in extracted microservices
        /// (everything except the Identity service itself) so they validate tokens issued by
        /// the central Identity service without sharing a symmetric secret.
        /// <para>
        /// The <paramref name="authority"/> argument is the base URL of the Identity service
        /// (e.g. <c>http://identity</c>, resolved via Aspire service discovery). The JWT
        /// middleware fetches <c>{authority}/.well-known/openid-configuration</c> on startup,
        /// which in turn points at <c>/.well-known/jwks.json</c> served by <c>MapJwksEndpoint</c>.
        /// </para>
        /// </summary>
        /// <param name="authority">The Identity service base URL (no trailing slash).</param>
        /// <param name="audience">The expected JWT audience claim.</param>
        /// <param name="configuration">The application configuration, read for <see cref="RequireHttpsMetadataConfigKey"/>.</param>
        /// <param name="environment">The host environment, which decides the default when nothing overrides it.</param>
        /// <param name="requireHttpsMetadata">
        /// Whether the metadata fetch must use HTTPS. Resolved in three steps: this argument when it
        /// is not <see langword="null"/>, then <see cref="RequireHttpsMetadataConfigKey"/>, then
        /// <see langword="true"/> everywhere except Development. A resolved
        /// <see langword="false"/> outside Development is legal (an internal-ingress h2c authority
        /// is the reason it stays reachable) but logs one startup warning naming the config key.
        /// </param>
        public IServiceCollection AddForwardedJwtBearer(
            string authority,
            string audience,
            IConfiguration configuration,
            IHostEnvironment environment,
            bool? requireHttpsMetadata = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(authority);
            ArgumentException.ThrowIfNullOrWhiteSpace(audience);
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(environment);

            var resolvedRequireHttpsMetadata = requireHttpsMetadata
                ?? configuration.GetValue<bool?>(RequireHttpsMetadataConfigKey)
                ?? !environment.IsDevelopment();

            if (!resolvedRequireHttpsMetadata && !environment.IsDevelopment())
            {
                services.TryAddEnumerable(
                    ServiceDescriptor.Singleton<IStartupFilter, InsecureJwtMetadataWarningStartupFilter>());
            }

            return services.AddForwardedJwtBearerCore(authority, audience, resolvedRequireHttpsMetadata);
        }

        private IServiceCollection AddForwardedJwtBearerCore(
            string authority,
            string audience,
            bool resolvedRequireHttpsMetadata)
        {
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.Authority = authority;
                    options.Audience = audience;
                    options.RequireHttpsMetadata = resolvedRequireHttpsMetadata;

                    options.TokenValidationParameters = new()
                    {
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        // ValidIssuer is intentionally NOT set here. The JWT bearer
                        // middleware derives it from the OIDC discovery document's
                        // "issuer" field (served by MapOidcDiscoveryEndpoint). This
                        // avoids a mismatch: authority is the Aspire service-discovery
                        // URL (e.g. "http://identity") while the token's iss claim is
                        // the public gateway URL (e.g. "https://localhost:6001").
                        ValidAudience = audience,
                        // Pin the signature algorithm to RS256. The JWKS path only ever
                        // validates Identity's asymmetric (RS256) tokens, so this is
                        // defense-in-depth against an algorithm-confusion swap (e.g.
                        // forging an HS256 token using the RSA public key as the HMAC
                        // secret), matching the explicit pin on the in-process
                        // BuildValidationParameters path.
                        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    };

                    // Same SignalR access_token query-string fallback as AddCommonAuthentication.
                    options.Events = new JwtBearerEvents
                    {
                        OnMessageReceived = context =>
                        {
                            var accessToken = context.Request.Query["access_token"];
                            if (!string.IsNullOrEmpty(accessToken)
                                && context.HttpContext.Request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
                            {
                                context.Token = accessToken;
                            }

                            return Task.CompletedTask;
                        }
                    };
                });

            services.AddAuthorizationPolicies();
            return services;
        }

        /// <summary>
        /// Registers JWT Bearer authentication and authorization policies, supporting both
        /// symmetric (HMAC-SHA256) and asymmetric (RSA-SHA256) signing modes selected via
        /// <see cref="JwtSettings.SigningAlgorithm"/>.
        /// <para>
        /// In the default <see cref="JwtSigningAlgorithm.RS256"/> mode, the validator loads the
        /// RSA public key from <see cref="JwtSettings.RsaPublicKeyPem"/>. In
        /// <see cref="JwtSigningAlgorithm.HS256"/> mode, a single-process monolith's validator uses
        /// the same Base64 HMAC secret as the issuer.
        /// For extracted services that should fetch the public key from the Identity
        /// service's JWKS endpoint at runtime, use <c>AddForwardedJwtBearer</c> instead.
        /// </para>
        /// </summary>
        public IServiceCollection AddCommonAuthentication(IConfiguration configuration)
        {
            services.AddOptions<JwtSettings>()
                .Bind(configuration.GetSection(JwtSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    var jwtSettings = configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>()
                        ?? throw new InvalidOperationException("JwtSettings section is not configured.");

                    options.TokenValidationParameters = BuildValidationParameters(jwtSettings);

                    // SignalR WebSocket connections cannot send HTTP headers — the JWT is
                    // passed as an "access_token" query-string parameter instead. Extract
                    // it here so the standard JWT middleware can authenticate hub requests.
                    options.Events = new JwtBearerEvents
                    {
                        OnMessageReceived = context =>
                        {
                            var accessToken = context.Request.Query["access_token"];
                            if (!string.IsNullOrEmpty(accessToken)
                                && context.HttpContext.Request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
                            {
                                context.Token = accessToken;
                            }

                            return Task.CompletedTask;
                        }
                    };
                });

            services.AddAuthorizationPolicies();

            return services;
        }
    }

    /// <summary>
    /// Decodes a Base64-encoded JWT signing key and validates that it meets the
    /// minimum length requirement for HMAC-SHA256 (256 bits / 32 bytes).
    /// </summary>
    internal static byte[] GetValidatedSigningKey(string base64Key)
    {
        var keyBytes = Convert.FromBase64String(base64Key);
        if (keyBytes.Length < 32)
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"JWT SecretForKey must be at least 256 bits (32 bytes) for HMAC-SHA256. Current key is {keyBytes.Length * 8} bits."));
        }

        return keyBytes;
    }

    /// <summary>
    /// Builds the <see cref="TokenValidationParameters"/> for the configured signing
    /// algorithm. RS256 deployments (the default) load the public key from
    /// <see cref="JwtSettings.RsaPublicKeyPem"/>; HS256 uses the Base64 HMAC secret. The validator pins
    /// <see cref="TokenValidationParameters.ValidAlgorithms"/> so an attacker cannot swap
    /// algorithms (e.g., signing an HS256 token with the RSA public key as the HMAC secret).
    /// </summary>
    internal static TokenValidationParameters BuildValidationParameters(JwtSettings jwtSettings)
    {
        if (jwtSettings.SigningAlgorithm == JwtSigningAlgorithm.RS256)
        {
            if (string.IsNullOrWhiteSpace(jwtSettings.RsaPublicKeyPem))
            {
                throw new InvalidOperationException(
                    "JwtSettings.RsaPublicKeyPem is required when SigningAlgorithm is RS256 and AddCommonAuthentication is used (in-process validation). For services that should fetch the public key via JWKS at runtime, use AddForwardedJwtBearer instead.");
            }

#pragma warning disable CA2000 // The RSA instance is captured by RsaSecurityKey which is held by JwtBearerOptions for the app lifetime.
            var validationRsa = RSA.Create();
#pragma warning restore CA2000
            validationRsa.ImportFromPem(jwtSettings.RsaPublicKeyPem);
            return new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwtSettings.Issuer,
                ValidAudience = jwtSettings.Audience,
                IssuerSigningKey = new RsaSecurityKey(validationRsa),
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            };
        }

        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidAudience = jwtSettings.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(
                GetValidatedSigningKey(
                    jwtSettings.SecretForKey
                    ?? throw new KeyNotFoundException("SecretForKey not found or invalid"))),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        };
    }
}

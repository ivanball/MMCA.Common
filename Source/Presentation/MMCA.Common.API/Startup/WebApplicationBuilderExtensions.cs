using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Asp.Versioning;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using MMCA.Common.API.Authorization;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.API.Startup;

/// <summary>
/// Shared WebAPI service registration extensions used by all downstream MMCA applications.
/// Consolidates identical builder-side setup (versioning, rate limiting, compression, CORS).
/// </summary>
public static class WebApplicationBuilderExtensions
{
    /// <summary>Default CORS policy name for production (allowed origins from config).</summary>
    public const string CorsPolicyAllowSpecificOrigins = "_allowSpecificOrigins";

    /// <summary>Default CORS policy name for development (any origin).</summary>
    public const string CorsPolicyAllowAll = "_allowAll";

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers header-based API versioning (v1.0 default) with MVC and API explorer support.
        /// </summary>
        public IServiceCollection AddCommonApiVersioning()
        {
            services.AddApiVersioning(options =>
            {
                options.DefaultApiVersion = new ApiVersion(1, 0);
                options.AssumeDefaultVersionWhenUnspecified = true;
                options.ReportApiVersions = true;
                options.ApiVersionReader = new HeaderApiVersionReader("api-version");
            }).AddMvc()
            .AddApiExplorer(options =>
            {
                options.GroupNameFormat = "'v'VVV";
                options.SubstituteApiVersionInUrl = true;
                options.DefaultApiVersion = new ApiVersion(1, 0);
                options.AssumeDefaultVersionWhenUnspecified = true;
            });

            return services;
        }

        /// <summary>
        /// Registers rate limiters: a global fixed-window limiter ("FixedPolicy") and a
        /// per-user fixed-window limiter ("UserPolicy") that partitions by authenticated user
        /// or IP address. This prevents a single user from exhausting the global quota.
        /// </summary>
        public IServiceCollection AddCommonRateLimiting(int permitLimit = 100, int queueLimit = 2, int perUserPermitLimit = 30) =>
            services.AddRateLimiter(options =>
            {
                options.AddFixedWindowLimiter("FixedPolicy", limiterOptions =>
                {
                    limiterOptions.Window = TimeSpan.FromMinutes(1);
                    limiterOptions.PermitLimit = permitLimit;
                    limiterOptions.QueueLimit = queueLimit;
                    limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                });

                options.AddPolicy("UserPolicy", httpContext =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: httpContext.User?.Identity?.Name
                            ?? httpContext.Connection.RemoteIpAddress?.ToString()
                            ?? "anonymous",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            Window = TimeSpan.FromMinutes(1),
                            PermitLimit = perUserPermitLimit,
                            QueueLimit = queueLimit,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                        }));
            });

        /// <summary>
        /// Registers Brotli + Gzip response compression for HTTPS responses.
        /// </summary>
        public IServiceCollection AddCommonResponseCompression()
        {
            services.AddResponseCompression(options =>
            {
                options.EnableForHttps = true;
                options.Providers.Add<BrotliCompressionProvider>();
                options.Providers.Add<GzipCompressionProvider>();
            });
            services.Configure<BrotliCompressionProviderOptions>(options =>
                options.Level = CompressionLevel.Fastest);
            services.Configure<GzipCompressionProviderOptions>(options =>
                options.Level = CompressionLevel.SmallestSize);

            return services;
        }

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
        /// <param name="requireHttpsMetadata">
        /// Whether the metadata fetch must use HTTPS. Defaults to <see langword="false"/> so
        /// service-discovery URLs (which are <c>http://</c>) work in dev. Production deployments
        /// should set this to <see langword="true"/>.
        /// </param>
        public IServiceCollection AddForwardedJwtBearer(
            string authority,
            string audience,
            bool requireHttpsMetadata = false)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(authority);
            ArgumentException.ThrowIfNullOrWhiteSpace(audience);

            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.Authority = authority;
                    options.Audience = audience;
                    options.RequireHttpsMetadata = requireHttpsMetadata;

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
        /// In monolith mode (the default <see cref="JwtSigningAlgorithm.HS256"/>), the
        /// validator uses the same Base64 HMAC secret as the issuer. In RS256 mode, the
        /// validator loads the RSA public key from <see cref="JwtSettings.RsaPublicKeyPem"/>.
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

        /// <summary>
        /// Registers two CORS policies: a restrictive one for production (origins from
        /// <c>Cors:AllowedOrigins</c> configuration) and an open one for development.
        /// </summary>
        public IServiceCollection AddCommonCors(IConfiguration configuration)
        {
            services.AddCors(options =>
            {
                options.AddPolicy(CorsPolicyAllowSpecificOrigins, policy =>
                {
                    var allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
                    policy.WithOrigins(allowedOrigins)
                          .WithHeaders("Content-Type", "Authorization", "x-signalr-user-agent", "x-requested-with")
                          .WithMethods("GET", "POST", "PUT", "DELETE", "PATCH")
                          .AllowCredentials();
                });
                options.AddPolicy(CorsPolicyAllowAll, policy =>
                    policy.AllowAnyOrigin()
                          .AllowAnyHeader()
                          .AllowAnyMethod());
            });

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
                $"JWT SecretForKey must be at least 256 bits (32 bytes) for HMAC-SHA256. Current key is {keyBytes.Length * 8} bits.");
        }

        return keyBytes;
    }

    /// <summary>
    /// Builds the <see cref="TokenValidationParameters"/> for the configured signing
    /// algorithm. RS256 deployments load the public key from <see cref="JwtSettings.RsaPublicKeyPem"/>;
    /// HS256 (default) uses the Base64 HMAC secret. The validator pins
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
                    ?? throw new System.Collections.Generic.KeyNotFoundException("SecretForKey not found or invalid"))),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        };
    }

}

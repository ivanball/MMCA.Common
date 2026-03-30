using System.IO.Compression;
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
        /// Registers JWT Bearer authentication with symmetric HMAC-SHA256 key and authorization policies.
        /// Binds <see cref="JwtSettings"/> from configuration and validates the signing key length.
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

                    options.TokenValidationParameters = new()
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
                                ?? throw new System.Collections.Generic.KeyNotFoundException("SecretForKey not found or invalid")))
                    };

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
                          .WithHeaders("Content-Type", "Authorization")
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
}

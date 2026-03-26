using System.IO.Compression;
using System.Threading.RateLimiting;
using Asp.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        /// Registers a fixed-window rate limiter with the "FixedPolicy" name.
        /// Default: 100 requests per minute, queue depth 2, oldest-first processing.
        /// </summary>
        public IServiceCollection AddCommonRateLimiting(int permitLimit = 100, int queueLimit = 2) =>
            services.AddRateLimiter(options => options.AddFixedWindowLimiter("FixedPolicy", limiterOptions =>
            {
                limiterOptions.Window = TimeSpan.FromMinutes(1);
                limiterOptions.PermitLimit = permitLimit;
                limiterOptions.QueueLimit = queueLimit;
                limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            }));

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
}

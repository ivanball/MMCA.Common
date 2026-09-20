using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using Asp.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMCA.Common.API.OpenApi;

namespace MMCA.Common.API.Startup;

/// <summary>
/// Shared WebAPI service registration extensions used by all downstream MMCA applications.
/// Consolidates identical builder-side setup (versioning, rate limiting, compression, CORS).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with multiple extension(T) blocks in one static class, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static partial class WebApplicationBuilderExtensions
{
    /// <summary>Default CORS policy name for production (allowed origins from config).</summary>
    public const string CorsPolicyAllowSpecificOrigins = "_allowSpecificOrigins";

    /// <summary>Default CORS policy name for development (any origin).</summary>
    public const string CorsPolicyAllowAll = "_allowAll";

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers header-based API versioning (v1.0 default) with MVC and API explorer support.
        /// Also installs <see cref="ApiParameterDescriptorBackfillProvider"/>, which keeps
        /// URL-segment-versioned routes from failing OpenAPI document generation.
        /// </summary>
        public IServiceCollection AddCommonApiVersioning()
        {
            // DefaultApiVersion is deliberately not set: 1.0 is already the framework default, and
            // the API explorer inherits both it and AssumeDefaultVersionWhenUnspecified from the
            // versioning options below (restating either one trips AV0011/AV0024).
            services.AddApiVersioning(options =>
            {
                options.AssumeDefaultVersionWhenUnspecified = true;
                options.ReportApiVersions = true;
                options.ApiVersionReader = new HeaderApiVersionReader("api-version");
            }).AddMvc()
            .AddApiExplorer(options =>
            {
                options.GroupNameFormat = "'v'VVV";
                options.SubstituteApiVersionInUrl = true;
            });

            services.AddApiParameterDescriptorBackfill();

            return services;
        }

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
            // Fastest for gzip too: these are dynamic per-request API payloads on fractional
            // vCPUs, so the CPU cost of SmallestSize outweighs the marginal size win for the
            // gzip-only-client minority (Brotli-capable clients never hit this provider).
            services.Configure<GzipCompressionProviderOptions>(options =>
                options.Level = CompressionLevel.Fastest);

            return services;
        }

        /// <summary>
        /// Registers OpenAPI document generation through the API-versioning builder, which creates
        /// one document per discovered API version named by the API explorer's
        /// <c>GroupNameFormat</c> (<c>'v'VVV</c>, so v1.0 is the <c>v1</c> document). Pair with
        /// <c>MapCommonOpenApi()</c> so each service serves <c>/openapi/v1.json</c> the same way.
        /// The parameterless <c>AddApiVersioning()</c> call only returns the builder; the options
        /// configured by <c>AddCommonApiVersioning</c> accumulate independently of call order.
        /// Also installs <see cref="ApiParameterDescriptorBackfillProvider"/>, so a host that opts
        /// into OpenAPI without calling <c>AddCommonApiVersioning</c> is guarded too.
        /// </summary>
        public IServiceCollection AddCommonOpenApi()
        {
            services.AddApiVersioning().AddOpenApi();
            services.AddApiParameterDescriptorBackfill();

            // Strongly typed identifiers document as the primitive they wrap (ADR-115). ConfigureAll
            // rather than a named document, because AddOpenApi above creates one OpenApiOptions per
            // discovered API version and the wire shape is identical in all of them. Inert in a host
            // that declares no wrappers.
            services.ConfigureAll<Microsoft.AspNetCore.OpenApi.OpenApiOptions>(options =>
            {
                options.AddSchemaTransformer(new StronglyTypedIdSchemaTransformer());
                options.AddOperationTransformer(new StronglyTypedIdParameterTransformer());
            });

            return services;
        }

        /// <summary>
        /// Registers the <see cref="ApiParameterDescriptorBackfillProvider"/> guard exactly once,
        /// however many of the registration helpers above a host calls.
        /// <see cref="ServiceCollectionDescriptorExtensions.TryAddEnumerable(IServiceCollection, ServiceDescriptor)"/>
        /// de-duplicates on the implementation type, so the repeated call is a no-op rather than a
        /// second pass over every API description.
        /// </summary>
        private void AddApiParameterDescriptorBackfill() =>
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<IApiDescriptionProvider, ApiParameterDescriptorBackfillProvider>());

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
#pragma warning disable S5122 // Allow-any-origin policy is only ever selected when app.Environment.IsDevelopment() (see WebApplicationExtensions.UseCommonMiddlewarePipeline); production uses CorsPolicyAllowSpecificOrigins above
                options.AddPolicy(CorsPolicyAllowAll, policy =>
                    policy.AllowAnyOrigin()
                          .AllowAnyHeader()
                          .AllowAnyMethod());
#pragma warning restore S5122
            });

            return services;
        }
    }
}

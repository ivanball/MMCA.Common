using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMCA.Common.API;
using MMCA.Common.API.Startup;
using MMCA.Common.API.Startup.Auth;
using MMCA.Common.Application;
using MMCA.Common.Application.Modules;
using MMCA.Common.Aspire;
using MMCA.Common.Aspire.Caching;
using MMCA.Common.Aspire.Kestrel;
using MMCA.Common.Aspire.Logging;
using MMCA.Common.Grpc;
using MMCA.Common.Infrastructure;

namespace MMCA.Common.ServiceHost;

/// <summary>
/// The composed service-host registration: the start-up list every extracted service host repeats,
/// in the one order that is correct for all of them (ADR-059), as one call. What differs between hosts
/// arrives through <see cref="MmcaServiceHostOptions"/>; vendor calls (Key Vault, the Azure Monitor
/// exporter, a cloud broker transport) arrive only as callbacks, so this package stays cloud-neutral.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with an extension(T) block in a static class, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class ServiceHostBuilderExtensions
{
    extension(WebApplicationBuilder builder)
    {
        /// <summary>
        /// Registers the composed service host, in this order: the configuration-source callback; the
        /// Kestrel endpoints with the health-probe listener; the Serilog bootstrap; the service
        /// defaults; the after-service-defaults callback; Redis caching (and the Redis output cache when
        /// asked for) with the two-level cache; the module host, whose loader logs through a bootstrap
        /// logger factory; the infrastructure health checks (a database is required); CORS, API
        /// versioning and rate limiting from configuration; the output cache with a no-cache base policy
        /// then the output-cache callback; response compression, the OpenAPI 3.1 pin on the <c>v1</c>
        /// document and <c>AddCommonOpenApi()</c>; forwarded JWT or in-process token validation; the
        /// exception handlers, the infrastructure and the API; the services callback; the
        /// broker-transport callback; the application pipeline (whose body is the pipeline callback);
        /// and finally the gRPC service defaults and the per-module health checks.
        /// <para>
        /// The host keeps its own <c>builder.Services.AddOpenApi();</c> line in Program.cs (before or
        /// after this call): the OpenAPI XML-comment generator attaches a project's controller
        /// summaries only at an <c>AddOpenApi</c> call site compiled in that project, so a
        /// registration made inside this package would publish the document without them.
        /// <c>UseMmcaServiceHostAsync</c> fails startup when the host did not make that call.
        /// </para>
        /// <para>
        /// After the call the application pipeline is sealed: anything the host registers afterwards
        /// (gRPC endpoint services, a service-token client, MCP) must not register command or query
        /// handlers.
        /// </para>
        /// </summary>
        /// <param name="options">The host-specific values and callbacks.</param>
        /// <returns>The module host and bootstrap logger factory, for <c>UseMmcaServiceHostAsync</c>.</returns>
        /// <exception cref="ArgumentNullException">The builder or <paramref name="options"/> is null.</exception>
        /// <exception cref="ArgumentException"><see cref="MmcaServiceHostOptions.ModuleAssemblies"/> is empty.</exception>
        public MmcaServiceHost AddMmcaServiceHost(MmcaServiceHostOptions options)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(options);
            if (options.ModuleAssemblies is not { Count: > 0 })
            {
                throw new ArgumentException(
                    "MmcaServiceHostOptions.ModuleAssemblies must name at least one module assembly.",
                    nameof(options));
            }

            // Configuration sources first: ConfigurationManager loads each source as it is added, so a
            // vault value is seen by every eager bind below (the probe port, the authority, the
            // database checks).
            options.ConfigureConfigurationSources?.Invoke(builder);

            builder.ConfigureEndpointsWithHealthProbe(options.Protocols, options.RedeclareCleartextEndpoint);

            // Serilog joins the logging providers before the OpenTelemetry one AddServiceDefaults adds.
            builder.AddCommonSerilog(options.LogFilePath);
            builder.AddServiceDefaults();
            options.AfterServiceDefaults?.Invoke(builder);

            var configuration = builder.Configuration;
            var services = builder.Services;

            builder.AddRedisCaching();
            if (options.UseRedisOutputCaching)
            {
                builder.AddRedisOutputCaching();
            }

            services.AddCommonHybridCacheWhenRedisConfigured(configuration);

            // The module loader keeps the logger it is given for the life of the host, so the factory
            // is kept on the result and disposed by UseMmcaServiceHostAsync when the application stops.
            var bootstrapLoggerFactory = SerilogHostExtensions.CreateBootstrapLoggerFactory();
            var moduleHost = builder.AddModuleHost(
                options.ModuleAssemblies,
                bootstrapLoggerFactory.CreateLogger<ModuleLoader>());

            builder.AddInfrastructureHealthChecks(requireDatabase: true);

            services.AddCommonCors(configuration);
            services.AddCommonApiVersioning();
            services.AddCommonRateLimiting(configuration);

            services.AddOutputCache(outputCache =>
            {
                outputCache.AddBasePolicy(policy => policy.NoCache());
                options.ConfigureOutputCache?.Invoke(outputCache);
            });

            services.AddCommonResponseCompression();

            // The document itself is the HOST's registration: the OpenAPI XML-comment generator
            // attaches a project's controller summaries only at an AddOpenApi call site compiled in
            // that project, so the host keeps its own builder.Services.AddOpenApi() line
            // (UseMmcaServiceHostAsync refuses to start without it). The version pin lives here, as
            // named options on the document MapCommonOpenApi serves, so a bare AddOpenApi() made
            // before or after this call still emits 3.1: ASP.NET Core 11 would otherwise emit 3.2,
            // and a committed host document is one contract whichever framework line built the host.
            services.Configure<OpenApiOptions>(
                ServiceHostOpenApi.DocumentName,
                openApi => openApi.OpenApiVersion = Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_1);
            services.AddCommonOpenApi();

            if (options.Authentication == ServiceHostAuthentication.TokenIssuer)
            {
                services.AddCommonAuthentication(configuration);
            }
            else
            {
                services.AddForwardedJwtBearer(
                    authority: configuration.GetRequiredJwtAuthority(),
                    audience: JwtAudience.RequireConfigured(configuration[JwtAudience.ConfigKey]),
                    configuration: configuration,
                    environment: builder.Environment);
            }

            services.AddCommonExceptionHandlers();
            services.AddInfrastructure(configuration);
            services.AddAPI(moduleHost.ModulesSettings);

            options.ConfigureServices?.Invoke(builder);

            // A transport must exist when AddBrokerMessaging (inside the pipeline) picks it.
            options.RegisterBrokerTransports?.Invoke(services);

            services.AddMmcaApplicationPipeline(pipeline => options.ConfigurePipeline(pipeline, moduleHost));

            services.AddGrpcServiceDefaults();

            // Last: the module checks can only see the modules discovery (inside the pipeline) found.
            services.AddModuleHealthChecks(moduleHost.ModuleLoader);

            return new MmcaServiceHost(moduleHost, bootstrapLoggerFactory);
        }
    }
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Localization;
using Microsoft.FeatureManagement;
using Microsoft.FeatureManagement.Mvc;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.FeatureManagement;
using MMCA.Common.API.Idempotency;
using MMCA.Common.API.JsonConverters;
using MMCA.Common.API.Localization;
using MMCA.Common.API.Middleware;
using MMCA.Common.API.Resources;
using MMCA.Common.API.SessionCookies;
using MMCA.Common.Application.Modules;
using MMCA.Common.Application.Settings;

namespace MMCA.Common.API;

/// <summary>
/// Dependency injection extensions for the Common API layer.
/// </summary>
public static class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers MVC controllers with JSON (<see cref="CurrencyJsonConverter"/>) and XML formatters,
        /// optional module-based controller filtering, and scoped action filters
        /// (<see cref="IdempotencyFilter"/>).
        /// </summary>
        /// <param name="modulesSettings">
        /// When provided, registers <see cref="ModuleControllerFeatureProvider"/> to exclude controllers
        /// belonging to disabled modules. Pass <see langword="null"/> to include all discovered controllers.
        /// </param>
        /// <param name="configuration">
        /// When provided, binds <see cref="IdempotencySettings"/> from the configuration section.
        /// Pass <see langword="null"/> to use default idempotency settings.
        /// </param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddAPI(ModulesSettings? modulesSettings = null, IConfiguration? configuration = null)
        {
            var mvcBuilder = services.AddControllers(options =>
                {
                    options.ReturnHttpNotAcceptable = false;
                    options.Filters.Add<UnhandledResultFailureFilter>();
                })
                .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new CurrencyJsonConverter()))
                .AddXmlDataContractSerializerFormatters();

            if (modulesSettings is not null)
            {
                mvcBuilder.ConfigureApplicationPartManager(manager =>
                    manager.FeatureProviders.Add(new ModuleControllerFeatureProvider(modulesSettings)));
            }

            if (configuration is not null)
            {
                services.AddOptions<IdempotencySettings>()
                    .Bind(configuration.GetSection(IdempotencySettings.SectionName))
                    .ValidateDataAnnotations()
                    .ValidateOnStart();
            }

            // Registered as scoped because they depend on scoped services (ICacheService, ICurrentUserService)
            services.AddScoped<IdempotencyFilter>();
            services.AddScoped<OwnerOrAdminFilter>();

            // Feature Management — registers IFeatureManager, IFeatureManagerSnapshot,
            // and built-in filters (Percentage, TimeWindow, Targeting).
            // Feature flags are read from the "FeatureManagement" configuration section.
            services.AddFeatureManagement();
            services.AddSingleton<IDisabledFeaturesHandler, DisabledFeatureHandler>();

            // Server-side error-message localization at the HTTP edge, keyed by Error.Code (ADR-027).
            services.AddErrorLocalization();

            return services;
        }

        /// <summary>
        /// Registers the edge error-localization seam (ADR-027): <see cref="IErrorLocalizer"/> plus the
        /// framework's own <see cref="ErrorResources"/> source. Called automatically by <c>AddAPI</c>.
        /// Modules add their own error translations additively via <see cref="AddErrorResources{TResource}"/>.
        /// </summary>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddErrorLocalization()
        {
            services.AddLocalization();
            services.TryAddSingleton<IErrorLocalizer, ErrorLocalizer>();
            services.AddErrorResources<ErrorResources>();
            return services;
        }

        /// <summary>
        /// Registers a module's resource type as an additional <see cref="ErrorResourceSource"/> so its
        /// error codes localize through the shared <see cref="IErrorLocalizer"/> (ADR-027). The
        /// <typeparamref name="TResource"/> anchor's <c>.resx</c> siblings are keyed by error <c>Code</c>.
        /// </summary>
        /// <typeparam name="TResource">The module's resource anchor type (co-located with its <c>.resx</c>).</typeparam>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddErrorResources<TResource>()
        {
            services.AddSingleton(serviceProvider => new ErrorResourceSource(
                serviceProvider.GetRequiredService<IStringLocalizerFactory>().Create(typeof(TResource))));
            return services;
        }

        /// <summary>
        /// Registers the standard exception handler pipeline with ProblemDetails support.
        /// Handlers are evaluated in registration order — most-specific first,
        /// with <see cref="GlobalExceptionHandler"/> as the final catch-all (500).
        /// </summary>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddCommonExceptionHandlers()
        {
            services.AddProblemDetails(options
                => options.CustomizeProblemDetails = context
                => context.ProblemDetails.Extensions.TryAdd("requestId", context.HttpContext.TraceIdentifier));
            services.AddExceptionHandler<OperationCanceledExceptionHandler>();
            services.AddExceptionHandler<DomainExceptionHandler>();
            services.AddExceptionHandler<DbUpdateExceptionHandler>();
            services.AddExceptionHandler<ValidationExceptionHandler>();
            services.AddExceptionHandler<GlobalExceptionHandler>();

            return services;
        }

        /// <summary>
        /// Registers the server-side session-cookie reader (used during SSR prerender so [Authorize] pages
        /// can resolve auth state before JS interop is available) plus the cookie session refresher that
        /// backs <c>/auth/session/token</c> and <c>UseCookieSessionRefresh()</c>. Call on the Blazor Server
        /// (UI.Web) host.
        /// </summary>
        /// <param name="apiBaseAddress">
        /// Absolute base address of the API/Gateway the host's server-side refresher calls (typically
        /// <c>ApiSettings.ApiEndpoint</c> — the internal endpoint, not the browser-facing one).
        /// </param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddServerAuthSessionCookie(string apiBaseAddress)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(apiBaseAddress);

            services.AddHttpContextAccessor();
            services.AddMemoryCache();
            services.TryAddScoped<CookieTokenReader>();

            services.AddHttpClient(CookieSessionRefresher.RefreshClientName, client =>
                client.BaseAddress = new Uri(apiBaseAddress, UriKind.Absolute));

            // Singleton: the refresher's in-flight map must be shared across requests for single-flight to work.
            services.TryAddSingleton<ICookieSessionRefresher, CookieSessionRefresher>();
            return services;
        }

        /// <summary>
        /// Registers per-module health checks based on <see cref="ModuleLoader"/> discovery results.
        /// Enabled modules report <see cref="HealthStatus.Healthy"/>;
        /// disabled modules report <see cref="HealthStatus.Degraded"/>.
        /// Health check names follow the pattern <c>module-{Name}</c> and are tagged with "module"
        /// for easy filtering via <c>/health?tag=module</c>.
        /// </summary>
        /// <param name="moduleLoader">
        /// The module loader containing discovered enabled and disabled modules.
        /// Must be called after <see cref="ModuleLoader.DiscoverAndRegister"/>.
        /// </param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddModuleHealthChecks(ModuleLoader moduleLoader)
        {
            var builder = services.AddHealthChecks();

            foreach (var name in moduleLoader.EnabledModules.Select(m => m.Name))
            {
                builder.AddCheck(
                    $"module-{name}",
                    () => HealthCheckResult.Healthy($"Module '{name}' is enabled"),
                    tags: ["module"]);
            }

            foreach (var name in moduleLoader.DisabledModuleNames)
            {
                var captured = name;
                builder.AddCheck(
                    $"module-{captured}",
                    () => HealthCheckResult.Degraded($"Module '{captured}' is disabled"),
                    tags: ["module"]);
            }

            return services;
        }
    }
}

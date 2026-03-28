using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.FeatureManagement;
using Microsoft.FeatureManagement.Mvc;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.FeatureManagement;
using MMCA.Common.API.Idempotency;
using MMCA.Common.API.JsonConverters;
using MMCA.Common.API.Middleware;
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

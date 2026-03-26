using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.Idempotency;
using MMCA.Common.API.JsonConverters;
using MMCA.Common.API.Middleware;
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
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddAPI(ModulesSettings? modulesSettings = null)
        {
            var mvcBuilder = services.AddControllers(options => options.ReturnHttpNotAcceptable = false)
                .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new CurrencyJsonConverter()))
                .AddXmlDataContractSerializerFormatters();

            if (modulesSettings is not null)
            {
                mvcBuilder.ConfigureApplicationPartManager(manager =>
                    manager.FeatureProviders.Add(new ModuleControllerFeatureProvider(modulesSettings)));
            }

            // Registered as scoped because they depend on scoped services (ICacheService, ICurrentUserService)
            services.AddScoped<IdempotencyFilter>();
            services.AddScoped<OwnerOrAdminFilter>();

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
    }
}

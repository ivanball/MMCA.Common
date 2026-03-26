using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Services;
using MMCA.Common.Application.Services.Query;
using MMCA.Common.Application.Settings;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application;

/// <summary>
/// Registers common application-layer services, command/query handler decorators,
/// and optional profiling wrappers into the DI container.
/// </summary>
public static class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers core application services (event dispatcher, navigation metadata, query pipeline).
        /// </summary>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddApplication()
        {
            services.TryAddSingleton<IApplicationSettings>(sp => sp.GetRequiredService<IOptions<ApplicationSettings>>().Value);

            services.TryAddSingleton<IDomainEventDispatcher, DomainEventDispatcher>();
            services.TryAddSingleton<INavigationMetadataProvider, NavigationMetadataProvider>();
            services.TryAddSingleton<IEntityQueryPipeline, EntityQueryPipeline>();

            // Register validators defined in MMCA.Common.Application (e.g. LoginRequestValidator,
            // RefreshTokenRequestValidator). Module-level ScanModuleApplicationServices only scans the
            // module's own assembly, so common validators must be registered here.
            services.AddValidatorsFromAssemblyContaining<ClassReference>();

            return services;
        }

        /// <summary>
        /// Registers command handler decorators. Must be called AFTER all modules have
        /// registered their concrete handlers so that Scrutor's TryDecorate can find them.
        /// <para>
        /// Decorator ordering matters: decorators are applied in reverse registration order
        /// (last registered = outermost wrapper). The resulting execution order is:
        /// <c>LoggingDecorator -> CachingDecorator -> TransactionalDecorator -> ConcreteHandler</c>.
        /// Logging is outermost so it captures the full pipeline duration including transaction
        /// and cache invalidation. Cache invalidation sits outside the transaction boundary
        /// so cache is only cleared after the transaction commits successfully.
        /// </para>
        /// </summary>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddApplicationDecorators()
        {
            // Registered first = innermost decorator (wraps the concrete handler directly)
            services.TryDecorate(typeof(ICommandHandler<,>), typeof(TransactionalCommandDecorator<,>));
            // Registered second = middle decorator (wraps the transactional decorator)
            services.TryDecorate(typeof(ICommandHandler<,>), typeof(CachingCommandDecorator<,>));
            // Registered third = outermost decorator (wraps caching, captures full pipeline)
            services.TryDecorate(typeof(ICommandHandler<,>), typeof(LoggingCommandDecorator<,>));
            services.TryDecorate(typeof(IQueryHandler<,>), typeof(CachingQueryDecorator<,>));
            services.TryDecorate(typeof(IQueryHandler<,>), typeof(LoggingQueryDecorator<,>));

            return services;
        }

        /// <summary>
        /// Scans a module assembly and registers all domain event handlers, DTO mappers,
        /// request mappers, command/query handlers, and FluentValidation validators found within it.
        /// This is the standard convention-based registration that every module calls.
        /// </summary>
        /// <typeparam name="TAssemblyMarker">A type in the module's Application assembly (typically <c>ClassReference</c>).</typeparam>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection ScanModuleApplicationServices<TAssemblyMarker>()
            where TAssemblyMarker : class
        {
            // Domain event handlers are singletons — they create their own DI scopes internally
            services.Scan(scan => scan
                .FromAssemblyOf<TAssemblyMarker>()
                .AddClasses(classes => classes.AssignableTo(typeof(IDomainEventHandler<>)))
                .AsImplementedInterfaces()
                .WithSingletonLifetime());

            services.Scan(scan => scan
                .FromAssemblyOf<TAssemblyMarker>()
                .AddClasses(classes => classes.AssignableTo(typeof(IEntityDTOMapper<,,>)))
                .AsSelfWithInterfaces()
                .WithScopedLifetime());

            services.Scan(scan => scan
                .FromAssemblyOf<TAssemblyMarker>()
                .AddClasses(classes => classes.AssignableTo(typeof(IEntityRequestMapper<,,>)))
                .AsSelfWithInterfaces()
                .WithScopedLifetime());

            services.Scan(scan => scan
                .FromAssemblyOf<TAssemblyMarker>()
                .AddClasses(classes => classes.AssignableTo(typeof(ICommandHandler<,>)))
                .AsImplementedInterfaces()
                .WithScopedLifetime());

            services.Scan(scan => scan
                .FromAssemblyOf<TAssemblyMarker>()
                .AddClasses(classes => classes.AssignableTo(typeof(IQueryHandler<,>)))
                .AsImplementedInterfaces()
                .WithScopedLifetime());

            services.AddValidatorsFromAssemblyContaining<TAssemblyMarker>();

            return services;
        }

        /// <summary>
        /// Registers MiniProfiler decorators for both command and query handlers.
        /// Must be called AFTER all modules have registered their concrete handlers.
        /// </summary>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddApplicationProfiling()
        {
            services.TryDecorate(typeof(ICommandHandler<,>), typeof(ProfilingCommandDecorator<,>));
            services.TryDecorate(typeof(IQueryHandler<,>), typeof(ProfilingQueryDecorator<,>));

            return services;
        }
    }
}

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
using MMCA.Common.Application.Validation;
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
        /// Registers command and query handler decorators. Must be called AFTER all modules have
        /// registered their concrete handlers so that Scrutor's TryDecorate can find them.
        /// <para>
        /// <b>Registration vs Execution Order:</b> Scrutor's <c>TryDecorate</c> applies decorators
        /// in reverse registration order — the last registered decorator becomes the outermost wrapper.
        /// </para>
        /// <para>
        /// <b>Command pipeline (nesting from outermost to innermost):</b>
        /// <code>
        ///   LoggingCommandDecorator              ← outermost: logs start/end, captures full pipeline duration
        ///     → CachingCommandDecorator          ← invalidates cache AFTER transaction commits
        ///       → ValidatingCommandDecorator     ← short-circuits with Result.Failure on validation errors
        ///         → TransactionalCommandDecorator  ← wraps handler in DB transaction (if ITransactional)
        ///           → ConcreteHandler            ← the actual business logic
        /// </code>
        /// </para>
        /// <para>
        /// <b>Query pipeline (nesting from outermost to innermost):</b>
        /// <code>
        ///   LoggingQueryDecorator            ← outermost: logs start/end, captures full pipeline duration
        ///     → CachingQueryDecorator        ← innermost: caches results (if IQueryCacheKeyProvider)
        ///       → ConcreteHandler            ← the actual query logic
        /// </code>
        /// </para>
        /// <para>
        /// <b>Design rationale:</b>
        /// <list type="bullet">
        /// <item>Logging is outermost so it measures the full pipeline including transaction + cache.</item>
        /// <item>Validation sits outside the transaction boundary so invalid commands never start
        /// a database transaction — saving resources on malformed requests.</item>
        /// <item>Cache invalidation sits outside validation so cache is only cleared after a valid,
        /// committed mutation — a rollback or validation failure leaves cache intact.</item>
        /// <item>On business failure (<see cref="Result"/>.<c>IsFailure</c>), the transaction still commits
        /// (no data was mutated) but cache invalidation is skipped.</item>
        /// <item>On exception, the transaction rolls back and the exception propagates through all decorators.</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddApplicationDecorators()
        {
            // ── Command decorators ──────────────────────────────────────
            // Registered first = innermost (wraps the concrete handler directly).
            // Registered last  = outermost (wraps all other decorators).
            services.TryDecorate(typeof(ICommandHandler<,>), typeof(TransactionalCommandDecorator<,>));   // innermost
            services.TryDecorate(typeof(ICommandHandler<,>), typeof(ValidatingCommandDecorator<,>));      // validates before transaction
            services.TryDecorate(typeof(ICommandHandler<,>), typeof(CachingCommandDecorator<,>));         // cache invalidation
            services.TryDecorate(typeof(ICommandHandler<,>), typeof(LoggingCommandDecorator<,>));         // outermost

            // ── Query decorators ────────────────────────────────────────
            services.TryDecorate(typeof(IQueryHandler<,>), typeof(CachingQueryDecorator<,>));             // innermost
            services.TryDecorate(typeof(IQueryHandler<,>), typeof(LoggingQueryDecorator<,>));             // outermost

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

            // Auto-register validators for commands that embed a request via ICommandWithRequest<T>.
            // Uses TryAdd — explicit IValidator<TCommand> from the line above takes precedence.
            services.AddAutoCommandRequestValidators<TAssemblyMarker>();

            return services;
        }

        /// <summary>
        /// Scans <typeparamref name="TAssemblyMarker"/>'s assembly for command types implementing
        /// <see cref="ICommandWithRequest{TRequest}"/> and registers a
        /// <see cref="CommandRequestValidator{TCommand,TRequest}"/> as <c>IValidator&lt;TCommand&gt;</c>
        /// for each — but only when no explicit validator was already registered.
        /// </summary>
        /// <typeparam name="TAssemblyMarker">A type in the module's Application assembly.</typeparam>
        /// <returns>The service collection for chaining.</returns>
        private IServiceCollection AddAutoCommandRequestValidators<TAssemblyMarker>()
            where TAssemblyMarker : class
        {
            var assembly = typeof(TAssemblyMarker).Assembly;

            foreach (var commandType in assembly.GetTypes())
            {
                var requestInterface = commandType.GetInterfaces()
                    .FirstOrDefault(i => i.IsGenericType
                        && i.GetGenericTypeDefinition() == typeof(ICommandWithRequest<>));

                if (requestInterface is null)
                    continue;

                var requestType = requestInterface.GetGenericArguments()[0];
                var validatorType = typeof(CommandRequestValidator<,>).MakeGenericType(commandType, requestType);
                var serviceType = typeof(IValidator<>).MakeGenericType(commandType);

                services.TryAddTransient(serviceType, validatorType);
            }

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

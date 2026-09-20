using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Mapping;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Crud;
using MMCA.Common.Application.Validation;

namespace MMCA.Common.Application;

public static partial class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Scans a module assembly and registers all domain event handlers, DTO mappers,
        /// request mappers, command/query handlers, and FluentValidation validators found within it.
        /// This is the standard convention-based registration that every module calls.
        /// </summary>
        /// <typeparam name="TAssemblyMarker">A type in the module's Application assembly (typically <c>ClassReference</c>).</typeparam>
        /// <returns>The service collection for chaining.</returns>
        /// <exception cref="InvalidOperationException">
        /// <c>AddApplicationDecorators()</c> has already run on this collection, so handlers registered
        /// now would never be wrapped.
        /// </exception>
        public IServiceCollection ScanModuleApplicationServices<TAssemblyMarker>()
            where TAssemblyMarker : class
            => services.ScanModuleApplicationServices(typeof(TAssemblyMarker).Assembly);

        /// <summary>
        /// Scans a module assembly and registers all domain event handlers, DTO mappers,
        /// request mappers, command/query handlers, and FluentValidation validators found within it.
        /// Assembly-typed overload of <see cref="ScanModuleApplicationServices{TAssemblyMarker}"/>, for
        /// callers that hold an <see cref="Assembly"/> rather than a marker type
        /// (composition helpers, host wiring driven by configuration).
        /// </summary>
        /// <param name="moduleAssembly">The module's Application assembly.</param>
        /// <returns>The service collection for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="moduleAssembly"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">
        /// <c>AddApplicationDecorators()</c> has already run on this collection, so handlers registered
        /// now would never be wrapped.
        /// </exception>
        public IServiceCollection ScanModuleApplicationServices(Assembly moduleAssembly)
        {
            ArgumentNullException.ThrowIfNull(moduleAssembly);
            ThrowIfPipelineSealed(services, nameof(ScanModuleApplicationServices));

            // Domain event handlers are singletons — they create their own DI scopes internally
            services.Scan(scan => scan
                .FromAssemblies(moduleAssembly)
                .AddClasses(classes => classes.AssignableTo(typeof(IDomainEventHandler<>)))
                .AsImplementedInterfaces()
                .WithSingletonLifetime());

            // Integration event handlers (cross-module) follow the same lifetime strategy
            services.Scan(scan => scan
                .FromAssemblies(moduleAssembly)
                .AddClasses(classes => classes.AssignableTo(typeof(IIntegrationEventHandler<>)))
                .AsImplementedInterfaces()
                .WithSingletonLifetime());

            services.Scan(scan => scan
                .FromAssemblies(moduleAssembly)
                .AddClasses(classes => classes.AssignableTo(typeof(IEntityDTOMapper<,,>)))
                .AsSelfWithInterfaces()
                .WithScopedLifetime());

            // DTO projectors are optional and opt-in: an entity that has one gets server-side
            // projection on its list reads, an entity that has none keeps materialize-then-map. They
            // are scanned beside the mappers so a module only has to write the projector class.
            services.Scan(scan => scan
                .FromAssemblies(moduleAssembly)
                .AddClasses(classes => classes.AssignableTo(typeof(IEntityDTOProjector<,,>)))
                .AsSelfWithInterfaces()
                .WithScopedLifetime());

            services.Scan(scan => scan
                .FromAssemblies(moduleAssembly)
                .AddClasses(classes => classes.AssignableTo(typeof(IEntityRequestMapper<,,>)))
                .AsSelfWithInterfaces()
                .WithScopedLifetime());

            // Update appliers are the write-side twin of the request mappers: one per aggregate,
            // wrapping that aggregate's guarded mutation methods so the generic UpdateEntityHandler
            // never has to know a field name. Scanned beside the mappers, so a module only writes
            // the applier class.
            services.Scan(scan => scan
                .FromAssemblies(moduleAssembly)
                .AddClasses(classes => classes.AssignableTo(typeof(IEntityUpdateApplier<,,>)))
                .AsSelfWithInterfaces()
                .WithScopedLifetime());

            // Command-aware appliers are the same contract widened to the whole command, for an
            // update that also depends on state the request body does not carry. Scanned the same
            // way, so a module only writes the applier class.
            services.Scan(scan => scan
                .FromAssemblies(moduleAssembly)
                .AddClasses(classes => classes.AssignableTo(typeof(IEntityUpdateCommandApplier<,,,>)))
                .AsSelfWithInterfaces()
                .WithScopedLifetime());

            services.Scan(scan => scan
                .FromAssemblies(moduleAssembly)
                .AddClasses(classes => classes.AssignableTo(typeof(ICommandHandler<,>)))
                .AsImplementedInterfaces()
                .WithScopedLifetime());

            services.Scan(scan => scan
                .FromAssemblies(moduleAssembly)
                .AddClasses(classes => classes.AssignableTo(typeof(IQueryHandler<,>)))
                .AsImplementedInterfaces()
                .WithScopedLifetime());

            services.AddValidatorsFromAssembly(moduleAssembly);

            // Auto-register validators for commands that embed a request via ICommandWithRequest<T>.
            // Uses TryAdd — explicit IValidator<TCommand> from the line above takes precedence.
            foreach (var commandType in moduleAssembly.GetTypes())
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
    }
}

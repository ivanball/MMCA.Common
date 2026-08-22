using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Services;
using MMCA.Common.Application.Services.Query;
using MMCA.Common.Application.Settings;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Application.Users.UseCases.ExportUserData;
using MMCA.Common.Application.Validation;
using MMCA.Common.Domain.Interfaces;
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

            // Composed view of every AddEventUpcaster registration. Always registered: with no
            // upcasters it is an empty registry whose operations are identity, and both delivery paths
            // (this dispatcher and the broker-side UpcastingIntegrationEventConsumer) can then depend
            // on it unconditionally (ADR-090).
            services.TryAddSingleton<IEventUpcasterRegistry, EventUpcasterRegistry>();

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
        ///   FeatureGateCommandDecorator              ← outermost: short-circuits if feature flag disabled
        ///     → AuthorizationCommandDecorator         ← short-circuits with Forbidden (if IRequiresPermission)
        ///       → LoggingCommandDecorator             ← logs start/end, captures full pipeline duration
        ///         → CachingCommandDecorator           ← invalidates cache AFTER transaction commits
        ///           → ValidatingCommandDecorator      ← short-circuits with Result.Failure on validation errors
        ///             → TimeoutCommandDecorator       ← applies the command's own budget (if IHasTimeout)
        ///               → TransactionalCommandDecorator ← wraps handler in DB transaction (if ITransactional)
        ///                 → ConcreteHandler            ← the actual business logic
        /// </code>
        /// </para>
        /// <para>
        /// <b>Query pipeline (nesting from outermost to innermost):</b>
        /// <code>
        ///   FeatureGateQueryDecorator           ← outermost: short-circuits if feature flag disabled
        ///     → AuthorizationQueryDecorator      ← short-circuits with Forbidden (if IRequiresPermission)
        ///       → LoggingQueryDecorator          ← logs start/end, captures full pipeline duration
        ///         → CachingQueryDecorator        ← caches results (if IQueryCacheable)
        ///           → TimeoutQueryDecorator      ← innermost: applies the query's own budget (if IHasTimeout)
        ///             → ConcreteHandler          ← the actual query logic
        /// </code>
        /// </para>
        /// <para>
        /// <b>Design rationale:</b>
        /// <list type="bullet">
        /// <item>Feature gating is outermost so disabled features are rejected immediately with zero
        /// overhead: no authorization, logging, caching, validation, or transaction work. It also
        /// sits outside authorization deliberately: a feature that is off must answer the same way
        /// for every caller rather than leaking which permission guards it.</item>
        /// <item>Authorization sits directly inside feature gating and outside caching, so a denied
        /// request neither reads nor populates the cache: a cache lookup ahead of the permission
        /// check would serve another caller's rows to a principal not allowed to run the query.</item>
        /// <item>Logging sits inside feature gating so it only measures enabled feature executions.</item>
        /// <item>Validation sits outside the transaction boundary so invalid commands never start
        /// a database transaction — saving resources on malformed requests.</item>
        /// <item>Cache invalidation sits outside validation so cache is only cleared after a valid,
        /// committed mutation — a rollback or validation failure leaves cache intact.</item>
        /// <item>The timeout budget sits inside validation and outside the transaction, so it covers
        /// the database work that actually hangs, does not charge the caller for validation, and
        /// cancels the transaction instead of leaving it open. On the query side it is innermost, so
        /// a cache hit is served without starting a budget at all.</item>
        /// <item>On business failure (<see cref="Result"/>.<c>IsFailure</c>), the transaction is rolled
        /// back (atomicity over partial persistence) and cache invalidation is skipped.</item>
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
            services.TryDecorate(typeof(ICommandHandler<,>), typeof(TimeoutCommandDecorator<,>));         // per-command execution budget
            services.TryDecorate(typeof(ICommandHandler<,>), typeof(ValidatingCommandDecorator<,>));      // validates before the budget and transaction
            services.TryDecorate(typeof(ICommandHandler<,>), typeof(CachingCommandDecorator<,>));         // cache invalidation
            services.TryDecorate(typeof(ICommandHandler<,>), typeof(LoggingCommandDecorator<,>));         // logging
            services.TryDecorate(typeof(ICommandHandler<,>), typeof(AuthorizationCommandDecorator<,>));   // permission check
            services.TryDecorate(typeof(ICommandHandler<,>), typeof(FeatureGateCommandDecorator<,>));     // outermost — feature flag check

            // ── Query decorators ────────────────────────────────────────
            services.TryDecorate(typeof(IQueryHandler<,>), typeof(TimeoutQueryDecorator<,>));             // innermost: per-query execution budget
            services.TryDecorate(typeof(IQueryHandler<,>), typeof(CachingQueryDecorator<,>));             // caching
            services.TryDecorate(typeof(IQueryHandler<,>), typeof(LoggingQueryDecorator<,>));             // logging
            services.TryDecorate(typeof(IQueryHandler<,>), typeof(AuthorizationQueryDecorator<,>));       // permission check
            services.TryDecorate(typeof(IQueryHandler<,>), typeof(FeatureGateQueryDecorator<,>));         // outermost — feature flag check

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

            // Integration event handlers (cross-module) follow the same lifetime strategy
            services.Scan(scan => scan
                .FromAssemblyOf<TAssemblyMarker>()
                .AddClasses(classes => classes.AssignableTo(typeof(IIntegrationEventHandler<>)))
                .AsImplementedInterfaces()
                .WithSingletonLifetime());

            services.Scan(scan => scan
                .FromAssemblyOf<TAssemblyMarker>()
                .AddClasses(classes => classes.AssignableTo(typeof(IEntityDTOMapper<,,>)))
                .AsSelfWithInterfaces()
                .WithScopedLifetime());

            // DTO projectors are optional and opt-in: an entity that has one gets server-side
            // projection on its list reads, an entity that has none keeps materialize-then-map. They
            // are scanned beside the mappers so a module only has to write the projector class.
            services.Scan(scan => scan
                .FromAssemblyOf<TAssemblyMarker>()
                .AddClasses(classes => classes.AssignableTo(typeof(IEntityDTOProjector<,,>)))
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
        /// Registers one <see cref="IUserDataExportSection"/> implementation as a contributor to the
        /// data-subject export.
        /// </summary>
        /// <typeparam name="TSection">The section implementation.</typeparam>
        /// <returns>The service collection for chaining.</returns>
        /// <remarks>
        /// <para>
        /// Sections accumulate: each module calls this for the data it owns and every registration is
        /// added to the same <c>IEnumerable&lt;IUserDataExportSection&gt;</c> the export handler fans
        /// out over, exactly like the scheduled-job and permission-registry idioms. The same type
        /// registered twice is added once, and registration order is the order the sections appear in
        /// the export document.
        /// </para>
        /// <para>
        /// The section is <b>scoped</b>: it runs inside the request's unit of work, so it may take
        /// scoped dependencies (repositories, gRPC clients, handlers).
        /// </para>
        /// <para>
        /// The export handler itself needs no registration here. Apps subclass
        /// <c>ExportUserDataHandlerBase&lt;TUser, TQuery&gt;</c> in their own Application assembly,
        /// and <see cref="ScanModuleApplicationServices{TAssemblyMarker}"/> picks the concrete
        /// subclass up as an <c>IQueryHandler</c> like any other handler.
        /// </para>
        /// </remarks>
        public IServiceCollection AddUserDataExportSection<TSection>()
            where TSection : class, IUserDataExportSection
        {
            services.TryAddScoped<TSection>();
            services.TryAddEnumerable(ServiceDescriptor.Scoped<IUserDataExportSection, TSection>());
            return services;
        }

        /// <summary>
        /// Registers one <see cref="IEventUpcaster{TSource, TTarget}"/> implementation, so a message
        /// still published (or still queued) as the retired contract <typeparamref name="TSource"/> is
        /// delivered to the handlers written against its successor <typeparamref name="TTarget"/>.
        /// </summary>
        /// <typeparam name="TSource">The retired event contract.</typeparam>
        /// <typeparam name="TTarget">The successor event contract. Must declare a higher <c>SchemaVersion</c>.</typeparam>
        /// <typeparam name="TUpcaster">The upcaster implementation.</typeparam>
        /// <returns>The service collection for chaining.</returns>
        /// <remarks>
        /// <para>
        /// ADR-010 makes a breaking event-shape change a NEW event type plus a consumer-side upcaster;
        /// this is the registration extension point for that policy (ADR-090). Registrations accumulate
        /// through <c>TryAddEnumerable</c>, the same idiom as <see cref="AddUserDataExportSection{TSection}"/>
        /// and the scheduled-job registry, and the same type registered twice is added once.
        /// </para>
        /// <para>
        /// <b>Singleton</b>, because upcasters are pure functions over an event instance, matching the
        /// handler lifetimes they feed. <typeparamref name="TSource"/> and <typeparamref name="TTarget"/>
        /// are named explicitly so the compiler checks the shape at the registration site instead of
        /// leaving a mismatch to fail at runtime.
        /// </para>
        /// <para>
        /// Chains compose: registering V1 to V2 and V2 to V3 delivers a V1 message to the V3 handler.
        /// Registering two upcasters for one source, mapping a type onto itself, or forming a cycle
        /// fails the host at startup with an exception naming the offenders.
        /// </para>
        /// <para>
        /// A monolith host needs only this call. A host that also consumes the retired contract over a
        /// broker adds <c>x.RegisterUpcastedIntegrationEventConsumer&lt;TSource&gt;()</c> beside its
        /// <c>x.RegisterIntegrationEventConsumer&lt;TTarget&gt;()</c>. Once every producer publishes the
        /// successor and the queues have drained, delete the upcaster, both registrations and
        /// eventually the retired type.
        /// </para>
        /// </remarks>
        public IServiceCollection AddEventUpcaster<TSource, TTarget, TUpcaster>()
            where TSource : class, IIntegrationEvent
            where TTarget : class, IIntegrationEvent
            where TUpcaster : class, IEventUpcaster<TSource, TTarget>
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IEventUpcaster, TUpcaster>());
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

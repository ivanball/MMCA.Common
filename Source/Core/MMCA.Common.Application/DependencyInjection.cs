using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Services;
using MMCA.Common.Application.Services.Query;
using MMCA.Common.Application.Settings;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Application.Users.UseCases.ExportUserData;
using MMCA.Common.Application.Validation;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.DTOs;

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
        ///   FeatureGateQueryDecorator             ← outermost: short-circuits if feature flag disabled
        ///     → AuthorizationQueryDecorator        ← short-circuits with Forbidden (if IRequiresPermission)
        ///       → LoggingQueryDecorator            ← logs start/end, captures full pipeline duration
        ///         → CachingQueryDecorator          ← caches results (if IQueryCacheable)
        ///           → ValidatingQueryDecorator     ← short-circuits with Result.Failure on validation errors
        ///             → TimeoutQueryDecorator      ← innermost: applies the query's own budget (if IHasTimeout)
        ///               → ConcreteHandler          ← the actual query logic
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
        /// a database transaction — saving resources on malformed requests. On the query side it sits
        /// INSIDE caching for a deliberate reason: a cached entry can only exist because the same
        /// query already passed validation when that entry was first produced, so re-validating on a
        /// cache hit spends work to reach a conclusion already reached.</item>
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
            ThrowIfPipelineSealed(services, nameof(AddApplicationDecorators));

            // The two authorization decorators below are registered unconditionally and take an
            // IPermissionRegistry, so the pipeline cannot activate at all without one. TryAdd, and
            // registered here rather than in AddApplication(), so a host that declared its grants
            // (AddAuthorizationPolicies / AddPermissions, both of which run before this call) keeps
            // its own registry, and a host with no permission model still resolves every handler.
            services.TryAddSingleton<IPermissionRegistry, UnconfiguredPermissionRegistry>();

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
            services.TryDecorate(typeof(IQueryHandler<,>), typeof(ValidatingQueryDecorator<,>));          // validates after the cache lookup, before the budget
            services.TryDecorate(typeof(IQueryHandler<,>), typeof(CachingQueryDecorator<,>));             // caching
            services.TryDecorate(typeof(IQueryHandler<,>), typeof(LoggingQueryDecorator<,>));             // logging
            services.TryDecorate(typeof(IQueryHandler<,>), typeof(AuthorizationQueryDecorator<,>));       // permission check
            services.TryDecorate(typeof(IQueryHandler<,>), typeof(FeatureGateQueryDecorator<,>));         // outermost — feature flag check

            SealPipeline(services);

            return services;
        }

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

        /// <summary>
        /// Registers the framework's generic write side for one aggregate: the create, update and
        /// delete handlers, each closed over that aggregate's own types. One call replaces the three
        /// hand-written handler classes a straightforward CRUD aggregate would otherwise need.
        /// </summary>
        /// <typeparam name="TEntity">The aggregate root.</typeparam>
        /// <typeparam name="TEntityDTO">The DTO the create and update handlers return.</typeparam>
        /// <typeparam name="TIdentifierType">The aggregate's primary key type.</typeparam>
        /// <typeparam name="TCreateRequest">The create request, which is also the create command.</typeparam>
        /// <typeparam name="TUpdateRequest">The update request carried by <c>UpdateEntityCommand</c>.</typeparam>
        /// <returns>The service collection for chaining.</returns>
        /// <exception cref="InvalidOperationException">
        /// <c>AddApplicationDecorators()</c> has already run on this collection, so handlers registered
        /// now would never be wrapped.
        /// </exception>
        /// <remarks>
        /// <para>
        /// <b>What the aggregate still owns.</b> Three collaborators, all picked up by
        /// <see cref="ScanModuleApplicationServices{TAssemblyMarker}"/>: an
        /// <see cref="IEntityRequestMapper{TEntity, TCreateRequest, TIdentifierType}"/> that runs the
        /// entity factory, an <see cref="IEntityUpdateApplier{TEntity, TUpdateRequest, TIdentifierType}"/>
        /// that calls the aggregate's guarded mutation methods, and an
        /// <see cref="IEntityDTOMapper{TEntity, TEntityDTO, TIdentifierType}"/>. The invariants and the
        /// domain events stay in the aggregate; nothing here writes a property.
        /// </para>
        /// <para>
        /// <b>Registrations are closed, not open-generic</b>, and use <c>TryAdd</c>. Closed, because
        /// Scrutor's <c>TryDecorate</c> wraps concrete service types: an open
        /// <c>ICommandHandler&lt;,&gt;</c> registration would resolve completely undecorated and
        /// <c>VerifyDecoratorPipeline()</c> could not see it. <c>TryAdd</c>, so an aggregate that
        /// outgrows one of the three (a create that needs a retry loop, a delete that has to load its
        /// children first) registers its own handler for that verb before this call and keeps the
        /// generic pair for the other two.
        /// </para>
        /// <para>
        /// Call it where a module registers its handlers, which means before
        /// <c>AddApplicationDecorators()</c>: inside the module's own <c>Register</c>, or inside the
        /// <c>AddMmcaApplicationPipeline(...)</c> callback.
        /// </para>
        /// <example>
        /// <code>
        /// services.AddEntityCrud&lt;Ticket, TicketDTO, TicketIdentifierType, TicketCreateRequest, TicketUpdateRequest&gt;();
        /// </code>
        /// </example>
        /// </remarks>
        public IServiceCollection AddEntityCrud<TEntity, TEntityDTO, TIdentifierType, TCreateRequest, TUpdateRequest>()
            where TEntity : AuditableAggregateRootEntity<TIdentifierType>
            where TEntityDTO : class, IBaseDTO<TIdentifierType>
            where TIdentifierType : notnull
            where TCreateRequest : class, ICreateRequest
        {
            ThrowIfPipelineSealed(services, nameof(AddEntityCrud));

            services.TryAddScoped<
                ICommandHandler<TCreateRequest, Result<TEntityDTO>>,
                CreateEntityHandler<TCreateRequest, TEntity, TIdentifierType, TEntityDTO>>();

            services.TryAddScoped<
                ICommandHandler<UpdateEntityCommand<TEntity, TUpdateRequest, TIdentifierType>, Result<TEntityDTO>>,
                UpdateEntityHandler<TEntity, TEntityDTO, TIdentifierType, TUpdateRequest>>();

            services.TryAddScoped<
                ICommandHandler<DeleteEntityCommand<TEntity, TIdentifierType>, Result>,
                DeleteEntityHandler<TEntity, TIdentifierType>>();

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

        /// <summary>
        /// Runs the framework's whole application-layer registration sequence in the one order that
        /// works, and then closes the pipeline so a later handler registration cannot slip in behind
        /// the decorators unnoticed.
        /// </summary>
        /// <param name="configure">
        /// Registers this host's handlers. Everything that puts an
        /// <see cref="UseCases.ICommandHandler{TCommand, TResult}"/> or
        /// <see cref="UseCases.IQueryHandler{TQuery, TResult}"/> into the container belongs here:
        /// module assembly scans, a <see cref="Modules.ModuleLoader"/> run, cross-service client
        /// registrations that replace a handler's dependencies, broker wiring. May be
        /// <see langword="null"/> for a host with no modules.
        /// </param>
        /// <returns>The service collection for chaining.</returns>
        /// <exception cref="InvalidOperationException">
        /// The decorator pipeline on this collection is already closed.
        /// </exception>
        /// <remarks>
        /// <para>
        /// Equivalent to writing, by hand and in this exact order:
        /// <c>AddApplication()</c>, then every module scan, then <c>AddApplicationDecorators()</c>.
        /// That last-ness is the load-bearing part: Scrutor's <c>TryDecorate</c> can only wrap
        /// registrations that already exist, so a handler registered after the decorators runs
        /// completely unwrapped (no feature gate, no authorization, no validation, no transaction)
        /// and nothing fails at startup to say so.
        /// </para>
        /// <para>
        /// Registrations that are not handlers (infrastructure, API, telemetry, options) can stay
        /// outside this call: their order relative to the decorators does not matter.
        /// </para>
        /// <example>
        /// <code>
        /// services.AddMmcaApplicationPipeline(pipeline => pipeline
        ///     .ScanModule&lt;TicketsClassReference&gt;()
        ///     .Register(s => moduleLoader.DiscoverAndRegister(s, configuration, appSettings, moduleSettings)));
        /// </code>
        /// </example>
        /// </remarks>
        public IServiceCollection AddMmcaApplicationPipeline(Action<MmcaApplicationPipelineBuilder>? configure = null)
        {
            ThrowIfPipelineSealed(services, nameof(AddMmcaApplicationPipeline));

            services.AddApplication();

            configure?.Invoke(new MmcaApplicationPipelineBuilder(services));

            return services.AddApplicationDecorators();
        }

        /// <summary>
        /// Asserts that every registered command and query handler is wrapped by the decorator
        /// pipeline, throwing an <see cref="InvalidOperationException"/> naming each one that is not.
        /// Never called automatically: this is the hook an architecture fitness test calls after
        /// running the host's own registration sequence.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The pipeline was never closed by <c>AddApplicationDecorators()</c>, or at least one handler
        /// registration is still the bare concrete handler.
        /// </exception>
        /// <remarks>
        /// <para>
        /// The check is registration-shape only: it inspects <see cref="ServiceDescriptor"/> entries
        /// and never builds a service provider, so a fitness test does not have to register a test
        /// double for every decorator dependency to run it.
        /// </para>
        /// <para>
        /// <b>What "wrapped" means concretely.</b> A module scan registers a handler by implementation
        /// type (<c>AsImplementedInterfaces</c>). Scrutor's <c>TryDecorate</c> then rewrites that entry
        /// into a factory over its own keyed copy of the original, so the surviving non-keyed
        /// descriptor for a decorated handler has no implementation type at all. An implementation
        /// type (or instance) still sitting on the effective registration is therefore proof that
        /// nothing ever wrapped it. Reading the outermost decorator's type back off the descriptor is
        /// not possible: after decoration it exists only inside a closure.
        /// </para>
        /// </remarks>
        public void VerifyDecoratorPipeline()
        {
            if (!IsPipelineSealed(services))
            {
                throw new InvalidOperationException(
                    "The MMCA decorator pipeline was never closed: AddApplicationDecorators() (or AddMmcaApplicationPipeline) "
                    + "has not run on this service collection, so no command or query handler is wrapped by the ADR-014 pipeline.");
            }

            // Last registration wins in Microsoft.Extensions.DependencyInjection, so the effective
            // descriptor per handler service type is the last non-keyed one.
            var effective = new Dictionary<Type, ServiceDescriptor>();

            foreach (var descriptor in services)
            {
                if (descriptor.IsKeyedService || !descriptor.ServiceType.IsGenericType)
                    continue;

                if (descriptor.ServiceType.ContainsGenericParameters)
                    continue;

                var definition = descriptor.ServiceType.GetGenericTypeDefinition();

                if (definition == typeof(ICommandHandler<,>) || definition == typeof(IQueryHandler<,>))
                    effective[descriptor.ServiceType] = descriptor;
            }

            var undecorated = effective
                .Where(entry => entry.Value.ImplementationFactory is null)
                .Select(entry => FormatUndecorated(entry.Key, entry.Value))
                .Order(StringComparer.Ordinal)
                .ToList();

            if (undecorated.Count > 0)
            {
                throw new InvalidOperationException(
                    "These command/query handler registrations are not wrapped by the ADR-014 decorator pipeline, so they run "
                    + "with no feature gate, authorization, logging, caching, validation, timeout or transaction. They were "
                    + "almost certainly registered AFTER AddApplicationDecorators():"
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, undecorated.Select(line => "  - " + line)));
            }
        }
    }

    /// <summary>
    /// Registered by <c>AddApplicationDecorators()</c> to record that the decorator pipeline has been
    /// closed on a given service collection. Private so it can never be resolved or depended on: its
    /// only job is to be present.
    /// </summary>
    private sealed class DecoratorPipelineSeal;

    private static bool IsPipelineSealed(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(DecoratorPipelineSeal))
                return true;
        }

        return false;
    }

    private static void SealPipeline(IServiceCollection services) =>
        services.TryAddSingleton(new DecoratorPipelineSeal());

    private static void ThrowIfPipelineSealed(IServiceCollection services, string callerName)
    {
        if (IsPipelineSealed(services))
        {
            throw new InvalidOperationException(
                $"'{callerName}' was called after AddApplicationDecorators() already closed the decorator pipeline on this "
                + "service collection. Scrutor's TryDecorate only wraps registrations that already exist, so anything "
                + "registered now would run completely undecorated. Move this call before AddApplicationDecorators(), or "
                + "compose the whole sequence with AddMmcaApplicationPipeline(...).");
        }
    }

    private static string FormatUndecorated(Type serviceType, ServiceDescriptor descriptor)
    {
        var arguments = serviceType.GetGenericArguments();
        var kind = serviceType.GetGenericTypeDefinition() == typeof(ICommandHandler<,>)
            ? "ICommandHandler"
            : "IQueryHandler";

        var implementation = descriptor.ImplementationType?.Name
            ?? (descriptor.ImplementationInstance is null ? "<unknown>" : descriptor.ImplementationInstance.GetType().Name);

        return $"{kind}<{arguments[0].Name}, {arguments[1].Name}> -> {implementation}";
    }
}

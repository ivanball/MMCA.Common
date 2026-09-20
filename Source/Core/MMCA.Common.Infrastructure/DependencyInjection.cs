using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure.Mail;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Infrastructure.Auth;
using MMCA.Common.Infrastructure.Context;
using MMCA.Common.Infrastructure.Mail;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Consumers;
using MMCA.Common.Infrastructure.Notifications.Live;
using MMCA.Common.Infrastructure.Notifications.Push;
using MMCA.Common.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox.Administration;
using MMCA.Common.Infrastructure.Persistence.Repositories;
using MMCA.Common.Infrastructure.Persistence.Repositories.Factory;
using MMCA.Common.Infrastructure.Persistence.Tenancy;
using MMCA.Common.Infrastructure.Storage;
using MMCA.Common.Shared.Identifiers;

namespace MMCA.Common.Infrastructure;

/// <summary>
/// Infrastructure layer DI registration. Uses C# preview extension types to add methods
/// directly to <see cref="IServiceCollection"/>.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with multiple extension(T) blocks in one static class, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static partial class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers all common infrastructure services: persistence (DbContexts, UoW, repositories),
        /// caching, authentication services, and settings bindings.
        /// </summary>
        /// <param name="configuration">Application configuration for binding options sections.</param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddInfrastructure(IConfiguration configuration)
        {
            services.TryAddSingleton<IEntityConfigurationAssemblyProvider, DefaultEntityConfigurationAssemblyProvider>();
            services.TryAddSingleton<IDataSourceService, DataSourceService>();

            // EF SaveChanges interceptors — registered as singletons because they are
            // stateless (per-save state is stored in ConditionalWeakTable keyed by context).
            services.TryAddSingleton<AuditSaveChangesInterceptor>();
            services.TryAddSingleton<DomainEventSaveChangesInterceptor>();

            // Always registered, like the other two: it is a no-op for every entity that does not
            // carry ITenantEntity, so a host that never adopts tenancy pays nothing, while a host
            // that does can never accidentally leave the write-side guard off.
            services.TryAddSingleton<TenantSaveChangesInterceptor>();

            services.AddOptions<ConnectionStringSettings>()
                .Bind(configuration.GetSection(ConnectionStringSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // The "a host must reach some database" rule cannot be a data annotation: it spans the
            // ConnectionStrings section AND the DataSources one, so a SQLite-only host that declares
            // its databases as named sources is legitimate while a host declaring none anywhere is
            // not. TryAddEnumerable, like the tenancy validator: two modules calling
            // AddInfrastructure must not run the same validation twice.
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IValidateOptions<ConnectionStringSettings>, ConnectionStringSettingsValidator>());

            // Named data sources for database-per-microservice routing. A root-level dictionary
            // section does not bind through the options pipeline — build the settings directly.
            services.TryAddSingleton(new DataSourcesSettings(
                configuration.GetSection(DataSourcesSettings.SectionName)
                    .Get<Dictionary<string, DataSourceEntrySettings>>()));
            services.TryAddSingleton<IDataSourceResolver, DataSourceResolver>();
            services.TryAddSingleton<IEntityDataSourceRegistry, EntityDataSourceRegistry>();

            services.AddOptions<SmtpSettings>()
                .Bind(configuration.GetSection(SmtpSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // Register our custom IDbContextFactory (scoped — one per request) and the singleton
            // physical factory that creates raw contexts per physical data source.
            // NEVER convert the physical factory to EF context pooling (AddPooledDbContextFactory):
            // each context instance carries per-source constructor state (PhysicalDataSource) that
            // pooling would silently reuse across databases.
            services.TryAddScoped<IDbContextFactory, DbContextFactory>();
            services.TryAddSingleton<IPhysicalDbContextFactory, PhysicalDbContextFactory>();

            services.TryAddSingleton<IQueryableExecutor, EFQueryableExecutor>();

            // Sibling of the queryable executor for the reads LINQ cannot express. Scoped, not
            // singleton: it reaches the scope's own context through IDbContextFactory, so a statement
            // shares the caller's connection and any transaction an ITransactional command opened.
            services.TryAddScoped<IRawSqlQueryExecutor, EFRawSqlQueryExecutor>();

            // Stateless classifier for a save rejected by a unique index, so a handler that lost an
            // insert race can answer with its own conflict instead of a raw 500. TryAdd, so a host
            // on another engine can register its own implementation first and keep it.
            services.TryAddSingleton<IUniqueConstraintViolationDetector, Persistence.SqlServerUniqueConstraintViolationDetector>();

            // Sibling classifier for a save rejected by a moved concurrency token, so a handler that
            // claims work by writing a row can recognise losing that claim without catching an EF
            // exception type in the Application layer. TryAdd, like the one above.
            services.TryAddSingleton<IConcurrencyConflictDetector, Persistence.EfCoreConcurrencyConflictDetector>();

            services.TryAddScoped(typeof(IRepository<,>), typeof(EFRepository<,>));
            services.TryAddScoped<IRepositoryFactory, RepositoryFactory>();
            services.TryAddScoped<IUnitOfWork, UnitOfWork>();

            // Scrutor assembly scan: discovers all EF entity configurations from this assembly
            // and registers them as their implemented interfaces (scoped, matching DbContext lifetime).
            services.Scan(scan => scan
                .FromAssemblyOf<ClassReference>()
                .AddClasses(classes => classes.AssignableTo(typeof(IEntityTypeConfigurationBase<,>)))
                .AsImplementedInterfaces()
                .WithScopedLifetime());

            services.AddCaching(configuration);

            // Relational persistence tuning (SQL command timeout). Optional: the defaults reproduce
            // the framework's previous implicit behavior, so a host that omits the section is unchanged.
            services.AddOptions<PersistenceSettings>()
                .Bind(configuration.GetSection(PersistenceSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            services.AddOptions<OutboxSettings>()
                .Bind(configuration.GetSection(OutboxSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            services.AddOptions<Auth.LoginProtectionSettings>()
                .Bind(configuration.GetSection(Auth.LoginProtectionSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();
            services.TryAddScoped<Application.Auth.ILoginProtectionService, Auth.LoginProtectionService>();

            services.AddOptions<Application.Auth.PasswordResetSettings>()
                .Bind(configuration.GetSection(Application.Auth.PasswordResetSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();
            services.TryAddScoped<Application.Auth.IPasswordResetTokenService, Auth.PasswordResetTokenService>();

            // Multi-device refresh sessions (BR-205/206). Scoped, like the unit of work it shares a
            // DbContext with, so a login and its session insert commit together.
            services.AddOptions<Application.Auth.RefreshSessionSettings>()
                .Bind(configuration.GetSection(Application.Auth.RefreshSessionSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();
            services.TryAddScoped<Application.Auth.IRefreshSessionStore, Persistence.Auth.EFRefreshSessionStore>();

            // Retention sweep, gated on the same flag that maps the table. Registering it
            // unconditionally would start an hourly sweep in every service of a modular host, all but
            // one of which has no RefreshSessions table to sweep.
            if (configuration.GetSection(Application.Auth.RefreshSessionSettings.SectionName)
                    .Get<Application.Auth.RefreshSessionSettings>()?.Enabled == true)
            {
                services.AddHostedService<Persistence.Auth.RefreshSessionCleanupService>();
            }

            services.AddOptions<MessageBusSettings>()
                .Bind(configuration.GetSection(MessageBusSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            services.AddOptions<JwksSettings>()
                .Bind(configuration.GetSection(JwksSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();
            services.TryAddSingleton<IJwksProvider, RsaJwksProvider>();

            // Fails the host at start on a bad event-upcaster registration graph (duplicate source,
            // self-map, cycle) instead of dead-lettering the first retired-contract message.
            // TryAddEnumerable, not AddHostedService: two modules calling AddInfrastructure must not
            // run the same validation twice.
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService, EventUpcasterStartupValidator>());

            services.TryAddSingleton<Persistence.Outbox.Processing.IOutboxSignal, Persistence.Outbox.Processing.OutboxSignal>();

            // The outbox is a transport decision, not a persistence one: a broker deployment cannot
            // deliver without it, while a single-process host dispatches every event inside the
            // process that raised it and pays two hosted services, a table and a poll loop for a hop
            // it never takes. MessageBusSettings.IsOutboxEnabled resolves that per transport (see
            // the settings for the full rationale); the OutboxMessages table stays mapped either
            // way, so flipping the flag is never a migration.
            var messageBusSettings = configuration.GetSection(MessageBusSettings.SectionName).Get<MessageBusSettings>()
                ?? new MessageBusSettings();
            EnsureOutboxAvailableForProvider(messageBusSettings);

            if (messageBusSettings.IsOutboxEnabled)
            {
                services.AddHostedService<Persistence.Outbox.Processing.OutboxProcessor>();
                services.AddHostedService<Persistence.Outbox.Administration.OutboxCleanupService>();
            }
            else
            {
                services.AddHostedService<Persistence.Outbox.Administration.OutboxDisabledNoticeService>();
            }

            // Operator surface over the same outbox tables: list, replay and count. Scoped, because
            // it creates one child scope per data source it visits and holds no state of its own.
            services.TryAddScoped<Application.Interfaces.Infrastructure.Persistence.IOutboxAdministration,
                Persistence.Outbox.Administration.OutboxAdministration>();

            AddInternalCommands(services, configuration);

            services.AddServices();

            // The impersonation decorator must wrap whatever ICurrentUserService is registered, so it
            // goes on AFTER AddServices() has run its TryAddScoped. Guarded so a host whose modules
            // each call AddInfrastructure does not stack one wrapper per call.
            if (!services.Any(d => d.ServiceType == typeof(Context.ScopedUserOverride)))
            {
                services.AddScoped<Context.ScopedUserOverride>();
                services.TryDecorate<ICurrentUserService, Context.ImpersonatingCurrentUserService>();
            }

            return services;
        }

        /// <summary>
        /// Enables multi-tenancy: binds the <c>Tenancy</c> settings section, validates it at
        /// startup, and turns on tenant resolution at the API edge.
        /// </summary>
        /// <param name="configuration">Application configuration for binding the <c>Tenancy</c> section.</param>
        /// <returns>The service collection for chaining.</returns>
        /// <remarks>
        /// <para>
        /// This call binds configuration; it does not install isolation. The <c>Tenant</c> query
        /// filter, <see cref="TenantSaveChangesInterceptor"/> and <see cref="ITenantContext"/> are
        /// always present and always inert until a tenant is resolved, so the framework can never be
        /// in the state where entities are marked <c>ITenantEntity</c> but only half the guard is
        /// wired.
        /// </para>
        /// <para>
        /// What this DOES switch on is resolution: with <c>Tenancy:Enabled</c> true,
        /// <c>TenantResolutionMiddleware</c> reads the tenant from the configured claim or header
        /// and, by default (<c>Tenancy:RequireTenant</c>), rejects a request that carries none.
        /// </para>
        /// <para>
        /// <b>Database-per-tenant is configuration only.</b> A tenant listed under
        /// <c>Tenancy:Tenants:{id}:DataSources:{sourceName}</c> is routed to its own database for
        /// that source; a tenant with no entry shares the database and is isolated by the filter.
        /// The override keys are PHYSICAL data source names, and startup validation fails the host
        /// when one names a source that does not exist, because the alternative is a silent fall
        /// back to the shared database.
        /// </para>
        /// </remarks>
        public IServiceCollection AddMultiTenancy(IConfiguration configuration)
        {
            services.AddOptions<TenancySettings>()
                .Bind(configuration.GetSection(TenancySettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // TryAddEnumerable: two modules calling this must not run the same validation twice.
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IValidateOptions<TenancySettings>, TenancySettingsValidator>());

            return services;
        }

        /// <summary>
        /// Registers application services: current user, token, password hashing, email, and time provider.
        /// </summary>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddServices()
        {
            services.AddHttpContextAccessor();

            services.TryAddScoped<ICorrelationContext, CorrelationContext>();

            // Registered whether or not the host called AddMultiTenancy: everything that reads it
            // (the query filter's accessor, the save interceptor, the caching decorators) treats an
            // unresolved tenant as "no tenancy", so the always-on registration costs one object per
            // scope and removes a whole class of "works until someone forgets the opt-in" bug.
            services.TryAddScoped<ITenantContext, TenantContext>();

            services.TryAddScoped<ICurrentUserService, CurrentUserService>();
            // Singleton: TokenService owns RSA handles (RS256) disposed in IDisposable.Dispose.
            // Scoped lifetime caused the underlying RSA to be disposed at end-of-request while
            // Microsoft.IdentityModel.Tokens' static CryptoProviderCache still held the cached
            // AsymmetricSignatureProvider that wrapped it, throwing ObjectDisposedException on
            // the next RS256 sign. Constructor only depends on IOptions of JwtSettings (singleton) and the
            // service is stateless after construction, so singleton is correct.
            services.TryAddSingleton<ITokenService, TokenService>();
            services.TryAddSingleton<IPasswordHasher, PasswordHasher>();
            services.TryAddScoped<IEventBus, InProcessEventBus>();

            // IMessageBus is the new abstraction used by OutboxProcessor (and, going forward, by
            // application code that publishes integration events). The default registration is
            // InProcessMessageBus — call AddBrokerMessaging(...) from a service host's Program.cs
            // to swap in MassTransit-backed BrokerMessageBus for microservice deployments.
            services.TryAddScoped<IMessageBus, InProcessMessageBus>();

            services.TryAddSingleton(TimeProvider.System);
            services.TryAddTransient<IEmailSender, SmtpEmailSender>();
            services.TryAddTransient<IPushNotificationSender, NullPushNotificationSender>();
            services.TryAddTransient<ILiveChannelPublisher, NullLiveChannelPublisher>();

            // Native push (ADR-044) defaults to inert no-ops; AddNativePushNotifications swaps in
            // the Azure Notification Hubs implementations when an enabled hub is configured.
            services.TryAddTransient<INativePushSender, NullNativePushSender>();
            services.TryAddTransient<IPushDeviceRegistrar, NullPushDeviceRegistrar>();

            // Managed file storage (ADR-045): unconfigured default swapped by
            // AddAzureBlobFileStorage; the image processor is dependency-free and always real.
            services.TryAddTransient<IFileStorageService, NullFileStorageService>();
            services.TryAddSingleton<IImageProcessor, ImageSharpImageProcessor>();

            return services;
        }

        /// <summary>
        /// Registers an additional assembly containing EF Core entity type configurations for discovery
        /// by <see cref="DefaultEntityConfigurationAssemblyProvider"/>. Use this when configurations reside in
        /// assemblies not automatically discovered (e.g., Common.Infrastructure feature modules).
        /// </summary>
        /// <param name="assembly">The assembly containing entity type configurations.</param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddEntityConfigurationAssembly(Assembly assembly)
        {
            services.Configure<EntityConfigurationOptions>(o =>
            {
                if (!o.AdditionalAssemblies.Contains(assembly))
                {
                    o.AdditionalAssemblies.Add(assembly);
                }
            });
            return services;
        }

        /// <summary>
        /// Opts this host into strongly typed identifiers (ADR-115). Scans
        /// <paramref name="identifierAssemblies"/> for every
        /// <c>IStronglyTypedId&lt;TSelf, TValue&gt;</c> implementation and does two things with the
        /// result: registers a <see cref="StronglyTypedIdRegistry"/> singleton, which
        /// <c>ApplicationDbContext.ConfigureConventions</c> turns into a pre-convention EF mapping so
        /// every wrapped property persists as its primitive on every engine, and registers a
        /// <see cref="System.ComponentModel.TypeConverter"/> per identifier so MVC binds
        /// <c>GET /orders/42</c> and <c>?filters[Id].value=42</c> against a wrapped column.
        /// <para>
        /// Calling it is the whole opt-in. A host that does not call it keeps the primitive
        /// identifier aliases (ADR-048/ADR-085) and nothing in the framework changes shape.
        /// </para>
        /// </summary>
        /// <param name="identifierAssemblies">The assemblies declaring the wrapper structs (typically each module's Shared assembly).</param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddStronglyTypedIds(params Assembly[] identifierAssemblies)
        {
            ArgumentNullException.ThrowIfNull(identifierAssemblies);

            var registry = new StronglyTypedIdRegistry(identifierAssemblies);

            foreach (var identifierType in registry.IdentifierTypes)
                StronglyTypedIdTypeConverters.Register(identifierType);

            services.TryAddSingleton(registry);

            return services;
        }
    }
}

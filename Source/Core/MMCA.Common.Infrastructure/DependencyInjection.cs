using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using MassTransit;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Infrastructure.Auth;
using MMCA.Common.Infrastructure.Caching;
using MMCA.Common.Infrastructure.Http;
using MMCA.Common.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Repositories;
using MMCA.Common.Infrastructure.Persistence.Repositories.Factory;
using MMCA.Common.Infrastructure.Services;
using MMCA.Common.Infrastructure.Settings;
using StackExchange.Redis;

namespace MMCA.Common.Infrastructure;

/// <summary>
/// Infrastructure layer DI registration. Uses C# preview extension types to add methods
/// directly to <see cref="IServiceCollection"/>.
/// </summary>
public static class DependencyInjection
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

            services.TryAddSingleton<IJwtSettings>(sp => sp.GetRequiredService<IOptions<JwtSettings>>().Value);

            services.AddOptions<ConnectionStringSettings>()
                .Bind(configuration.GetSection(ConnectionStringSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();
            services.TryAddSingleton<IConnectionStringSettings>(sp => sp.GetRequiredService<IOptions<ConnectionStringSettings>>().Value);

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
            services.TryAddSingleton<ISmtpSettings>(sp => sp.GetRequiredService<IOptions<SmtpSettings>>().Value);

            // Register our custom IDbContextFactory (scoped — one per request) and the singleton
            // physical factory that creates raw contexts per physical data source.
            // NEVER convert the physical factory to EF context pooling (AddPooledDbContextFactory):
            // each context instance carries per-source constructor state (PhysicalDataSource) that
            // pooling would silently reuse across databases.
            services.TryAddScoped<IDbContextFactory, DbContextFactory>();
            services.TryAddSingleton<IPhysicalDbContextFactory, PhysicalDbContextFactory>();

            // EF-style IDbContextFactory<T> adapters bound to each engine's Default physical
            // source — preserves the DI surface for ApplicationDbContextEFFactory and tests.
            services.TryAddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<CosmosDbContext>, DefaultCosmosDbContextFactory>();
            services.TryAddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<SqliteDbContext>, DefaultSqliteDbContextFactory>();
            services.TryAddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<SQLServerDbContext>, DefaultSqlServerDbContextFactory>();

            // Dual factory: our IDbContextFactory manages multi-DB routing, while this adapter satisfies
            // consumers that require EF Core's standard IDbContextFactory<ApplicationDbContext>.
            services.TryAddScoped<Microsoft.EntityFrameworkCore.IDbContextFactory<ApplicationDbContext>, ApplicationDbContextEFFactory>();

            services.TryAddSingleton<IQueryableExecutor, EFQueryableExecutor>();

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

            services.AddOptions<OutboxSettings>()
                .Bind(configuration.GetSection(OutboxSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            services.AddOptions<Auth.LoginProtectionSettings>()
                .Bind(configuration.GetSection(Auth.LoginProtectionSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();
            services.TryAddScoped<Application.Auth.ILoginProtectionService, Auth.LoginProtectionService>();

            services.AddOptions<MessageBusSettings>()
                .Bind(configuration.GetSection(MessageBusSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            services.AddOptions<JwksSettings>()
                .Bind(configuration.GetSection(JwksSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();
            services.TryAddSingleton<IJwksProvider, RsaJwksProvider>();

            services.TryAddSingleton<Persistence.Outbox.IOutboxSignal, Persistence.Outbox.OutboxSignal>();
            services.AddHostedService<Persistence.Outbox.OutboxProcessor>();
            services.AddHostedService<Persistence.Outbox.OutboxCleanupService>();

            services.AddServices();

            return services;
        }

        /// <summary>
        /// Registers the cache service. Uses distributed cache (e.g. Redis registered by Aspire)
        /// when available; otherwise falls back to in-memory cache.
        /// </summary>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddCaching(IConfiguration? configuration = null)
        {
            services.AddMemoryCache();

            // Optional key namespace so services sharing one cache instance cannot collide
            // (Cache:KeyPrefix). Absent configuration leaves keys exactly as callers write them.
            if (configuration is not null)
            {
                services.Configure<CacheKeyPrefixOptions>(configuration.GetSection(CacheKeyPrefixOptions.SectionName));
            }

            services.TryAddSingleton<ICacheService>(sp =>
            {
                var distributedCache = sp.GetService<IDistributedCache>();
                if (distributedCache is not null and not MemoryDistributedCache)
                {
                    var multiplexer = sp.GetService<IConnectionMultiplexer>();
                    var logger = sp.GetService<ILogger<DistributedCacheService>>()
                        ?? NullLogger<DistributedCacheService>.Instance;
                    var keyNamespace = CacheKeyNamespace.From(sp.GetService<IOptions<CacheKeyPrefixOptions>>());
                    return new DistributedCacheService(distributedCache, logger, multiplexer, keyNamespace);
                }

                // In-process: the keyspace is private to this process, so no prefix is needed.
                return new MemoryCacheService(sp.GetRequiredService<IMemoryCache>());
            });

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
            services.TryAddScoped<ICurrentUserService, CurrentUserService>();
            // Singleton: TokenService owns RSA handles (RS256) disposed in IDisposable.Dispose.
            // Scoped lifetime caused the underlying RSA to be disposed at end-of-request while
            // Microsoft.IdentityModel.Tokens' static CryptoProviderCache still held the cached
            // AsymmetricSignatureProvider that wrapped it, throwing ObjectDisposedException on
            // the next RS256 sign. Constructor only depends on IJwtSettings (singleton) and the
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
        /// Registers the Notification module's EF Core entity configurations (PushNotification, UserNotification)
        /// so they are discovered during model creation.
        /// </summary>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddNotificationInfrastructure()
        {
            services.AddEntityConfigurationAssembly(
                typeof(Persistence.Configuration.EntityTypeConfiguration.Notifications.PushNotificationConfiguration).Assembly);
            return services;
        }

        /// <summary>
        /// Registers SignalR push notification services, replacing the default <see cref="NullPushNotificationSender"/>
        /// with <see cref="SignalRPushNotificationSender"/> and the default <see cref="NullLiveChannelPublisher"/>
        /// with <see cref="SignalRLiveChannelPublisher"/>. Optionally configures a Redis backplane when a Redis
        /// connection string is available.
        /// </summary>
        /// <param name="configuration">Application configuration for binding settings and detecting Redis.</param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddPushNotifications(IConfiguration configuration)
        {
            services.AddOptions<PushNotificationSettings>()
                .Bind(configuration.GetSection(PushNotificationSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();
            services.TryAddSingleton<IPushNotificationSettings>(sp =>
                sp.GetRequiredService<IOptions<PushNotificationSettings>>().Value);

            var signalRBuilder = services.AddSignalR();

            var redisConnectionString = configuration.GetConnectionString("redis");
            if (!string.IsNullOrEmpty(redisConnectionString))
            {
                signalRBuilder.AddStackExchangeRedis(redisConnectionString);
            }

            // Replace the default Null implementations with the SignalR-backed ones.
            services.AddTransient<IPushNotificationSender, SignalRPushNotificationSender>();
            services.AddTransient<ILiveChannelPublisher, SignalRLiveChannelPublisher>();
            services.TryAddSingleton<IUserIdProvider, ClaimBasedUserIdProvider>();

            return services;
        }

        /// <summary>
        /// Registers OS-level native push delivery through Azure Notification Hubs (ADR-044),
        /// replacing the default <see cref="NullNativePushSender"/>/<see cref="NullPushDeviceRegistrar"/>
        /// pair. Reads the <c>NativePush</c> section (<c>Enabled</c>, <c>ConnectionString</c>,
        /// <c>HubName</c>); when disabled or incomplete the call is a no-op, so hosts register it
        /// unconditionally and deployments switch the channel on by configuration alone (the hub
        /// itself is provisioned before its FCM/APNs credentials exist).
        /// </summary>
        /// <param name="configuration">Application configuration providing the <c>NativePush</c> section.</param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddNativePushNotifications(IConfiguration configuration)
        {
            services.AddOptions<NativePushSettings>()
                .Bind(configuration.GetSection(NativePushSettings.SectionName));

            var settings = configuration.GetSection(NativePushSettings.SectionName).Get<NativePushSettings>();
            if (settings is not { Enabled: true }
                || string.IsNullOrWhiteSpace(settings.ConnectionString)
                || string.IsNullOrWhiteSpace(settings.HubName))
            {
                return services;
            }

            services.TryAddSingleton<Microsoft.Azure.NotificationHubs.INotificationHubClient>(_ =>
                Microsoft.Azure.NotificationHubs.NotificationHubClient.CreateClientFromConnectionString(
                    settings.ConnectionString, settings.HubName));
            services.AddTransient<INativePushSender, AzureNotificationHubNativePushSender>();
            services.AddTransient<IPushDeviceRegistrar, AzureNotificationHubDeviceRegistrar>();

            return services;
        }

        /// <summary>
        /// Registers Azure Blob Storage as the <see cref="IFileStorageService"/> (ADR-045),
        /// replacing the unconfigured <see cref="NullFileStorageService"/> default. Reads the
        /// <c>FileStorage</c> section: <c>ServiceUri</c> (managed-identity auth via
        /// DefaultAzureCredential, the production path) or <c>ConnectionString</c> (local
        /// Azurite), plus the required <c>ContainerName</c>. An incomplete section makes this a
        /// no-op, so hosts register it unconditionally.
        /// </summary>
        /// <param name="configuration">Application configuration providing the <c>FileStorage</c> section.</param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddAzureBlobFileStorage(IConfiguration configuration)
        {
            services.AddOptions<FileStorageSettings>()
                .Bind(configuration.GetSection(FileStorageSettings.SectionName));

            var settings = configuration.GetSection(FileStorageSettings.SectionName).Get<FileStorageSettings>();
            if (settings is null || string.IsNullOrWhiteSpace(settings.ContainerName))
            {
                return services;
            }

            // An empty-string ServiceUri binds to a RELATIVE Uri; only an absolute one counts.
            var hasServiceUri = settings.ServiceUri is { IsAbsoluteUri: true };
            if (!hasServiceUri && string.IsNullOrWhiteSpace(settings.ConnectionString))
            {
                return services;
            }

            services.TryAddSingleton(_ =>
            {
                var serviceClient = hasServiceUri
                    ? new Azure.Storage.Blobs.BlobServiceClient(settings.ServiceUri, new Azure.Identity.DefaultAzureCredential())
                    : new Azure.Storage.Blobs.BlobServiceClient(settings.ConnectionString);
                return serviceClient.GetBlobContainerClient(settings.ContainerName);
            });
            services.AddTransient<IFileStorageService, AzureBlobFileStorageService>();

            return services;
        }

        /// <summary>
        /// Replaces the default in-process <see cref="IMessageBus"/> registration with a
        /// MassTransit-backed <see cref="BrokerMessageBus"/>. Call this from a microservice
        /// host's <c>Program.cs</c> AFTER <c>AddInfrastructure(configuration)</c>.
        /// <para>
        /// The transport is selected by <see cref="MessageBusSettings.Provider"/>:
        /// <list type="bullet">
        ///   <item><see cref="MessageBusProvider.RabbitMq"/> — local dev (Aspire RabbitMQ container).</item>
        ///   <item><see cref="MessageBusProvider.AzureServiceBus"/> — production deployments.</item>
        ///   <item><see cref="MessageBusProvider.InProcess"/> — no-op; this method returns without
        ///   modifying the container, leaving the default <see cref="InProcessMessageBus"/> in place.</item>
        /// </list>
        /// </para>
        /// <para>
        /// Consumer registration is the responsibility of each service: pass an action that
        /// calls <c>x.AddConsumer&lt;TConsumer&gt;()</c> for every <c>IIntegrationEventHandler&lt;T&gt;</c>
        /// or <c>IConsumer&lt;T&gt;</c> implementation in the service.
        /// </para>
        /// </summary>
        /// <param name="configuration">Application configuration providing the <c>MessageBus</c> section.</param>
        /// <param name="configureConsumers">Optional callback for registering MassTransit consumers.</param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddBrokerMessaging(
            IConfiguration configuration,
            Action<IBusRegistrationConfigurator>? configureConsumers = null)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            var settings = configuration.GetSection(MessageBusSettings.SectionName).Get<MessageBusSettings>()
                ?? new MessageBusSettings();

            if (settings.Provider == MessageBusProvider.InProcess)
            {
                return services;
            }

            var connectionString = ResolveBrokerConnectionString(configuration, settings);

            services.AddMassTransit(x =>
            {
                if (!string.IsNullOrWhiteSpace(settings.EndpointPrefix))
                {
                    x.SetKebabCaseEndpointNameFormatter();
                }

                configureConsumers?.Invoke(x);
                ConfigureBrokerTransport(x, settings, connectionString);
            });

            // Swap the default in-process bus for the broker-backed one. Use Replace so we
            // overwrite the AddServices() registration rather than appending a second one.
            services.Replace(ServiceDescriptor.Scoped<IMessageBus, BrokerMessageBus>());

            // Also replace IEventBus so application code that publishes integration events
            // (via IEventBus.PublishAsync) writes to the outbox
            // and signals the OutboxProcessor — but does NOT dispatch in-process. The
            // OutboxProcessor's broker-publish path becomes the only delivery channel.
            services.Replace(ServiceDescriptor.Scoped<IEventBus, BrokerEventBus>());

            // Consumer-side idempotency: EfInboxStore is opt-in and requires the InboxMessages
            // table. When disabled, the no-op store keeps consumer behavior unchanged.
            if (settings.EnableInbox)
            {
                services.TryAddScoped<Persistence.Inbox.IInboxStore, Persistence.Inbox.EfInboxStore>();
            }
            else
            {
                services.TryAddSingleton<Persistence.Inbox.IInboxStore, Persistence.Inbox.NoOpInboxStore>();
            }

            return services;
        }

        /// <summary>
        /// Registers a typed service client (<typeparamref name="TInterface"/> →
        /// <typeparamref name="TImplementation"/>) backed by an <see cref="HttpClient"/> wired
        /// to Aspire service discovery (<c>http://{serviceName}</c>), Polly resilience (matching
        /// the standard handler from <c>AddServiceDefaults</c>), and the
        /// <see cref="JwtForwardingDelegatingHandler"/> for forwarding the inbound JWT bearer
        /// token to the downstream service.
        /// <para>
        /// Use this for HTTP-based cross-service contracts that don't warrant a gRPC binding —
        /// e.g., webhook receivers, public REST endpoints, or third-party API wrappers.
        /// gRPC is preferred for service-to-service contracts; see
        /// <c>MMCA.Common.Grpc.AddTypedGrpcClient&lt;T&gt;</c>.
        /// </para>
        /// </summary>
        /// <typeparam name="TInterface">The contract interface that consumer code depends on.</typeparam>
        /// <typeparam name="TImplementation">The class implementing the interface, taking <see cref="HttpClient"/> in its constructor.</typeparam>
        /// <param name="serviceName">The Aspire service-discovery name (e.g. <c>"identity"</c>).</param>
        /// <returns>The <see cref="IHttpClientBuilder"/> for further customization.</returns>
        public IHttpClientBuilder AddTypedServiceClient<TInterface, TImplementation>(string serviceName)
            where TInterface : class
            where TImplementation : class, TInterface
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

            services.AddHttpContextAccessor();
            services.TryAddTransient<JwtForwardingDelegatingHandler>();

#pragma warning disable S5332 // Deliberate h2c-style cleartext service-discovery address for in-cluster HTTP calls, same design as MMCA.Common.Grpc.AddTypedGrpcClient
            var builder = services.AddHttpClient<TInterface, TImplementation>(client =>
                    client.BaseAddress = new Uri($"http://{serviceName}"))
                .AddHttpMessageHandler<JwtForwardingDelegatingHandler>();
#pragma warning restore S5332

            builder.AddStandardResilienceHandler();
            return builder;
        }
    }

    /// <summary>
    /// Resolves the broker connection string. Order of precedence:
    /// <list type="number">
    ///   <item><c>MessageBus:ConnectionString</c> — explicit override in appsettings/secrets.</item>
    ///   <item><c>ConnectionStrings:rabbitmq</c> — Aspire injects this via <c>WithReference(broker)</c>.</item>
    ///   <item><c>ConnectionStrings:messaging</c> — alternative Aspire convention.</item>
    /// </list>
    /// Without this fallback, MassTransit defaults to <c>localhost:5672</c> and fails to reach
    /// the Aspire-allocated broker container port.
    /// </summary>
    [SuppressMessage(
        "Style",
        "IDE0051:Remove unused private members",
        Justification = "Called from AddBrokerMessaging inside the extension(IServiceCollection services) block above. The IDE0051 analyzer in .NET SDK 10.0.201+ does not see references that cross the boundary between a C# preview extension type block and outer-scope private members of the same containing class, so it reports a false positive. The local SDK 10.0.104 analyzer correctly resolves the call. Remove this suppression once Roslyn fixes the cross-block reference tracking.")]
    private static string? ResolveBrokerConnectionString(IConfiguration configuration, MessageBusSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ConnectionString))
        {
            return settings.ConnectionString;
        }

        return configuration.GetConnectionString("rabbitmq")
            ?? configuration.GetConnectionString("messaging");
    }

    /// <summary>
    /// Wires MassTransit to the configured broker transport using the resolved connection string.
    /// Every receive endpoint gets an exponential-backoff <c>UseMessageRetry</c> policy (configured
    /// by <see cref="MessageBusSettings.RetryLimit"/> and friends) so a transient handler failure is
    /// retried in-process instead of dead-lettering on the first exception.
    /// Extracted out of <c>AddBrokerMessaging</c> to keep that method's cyclomatic complexity
    /// below the analyzer threshold.
    /// </summary>
    /// <remarks>
    /// Only in-process retry (<c>UseMessageRetry</c>) is configured — not <c>UseDelayedRedelivery</c>,
    /// which on RabbitMQ requires the delayed-message-exchange plugin that the Aspire RabbitMQ
    /// container does not ship. A consumer that needs broker-level delayed redelivery can layer it
    /// on per-endpoint after installing the plugin (or on Azure Service Bus, which supports it natively).
    /// </remarks>
    [SuppressMessage(
        "Style",
        "IDE0051:Remove unused private members",
        Justification = "Called from AddBrokerMessaging inside the extension(IServiceCollection services) block above. The IDE0051 analyzer in .NET SDK 10.0.201+ does not see references that cross the boundary between a C# preview extension type block and outer-scope private members of the same containing class, so it reports a false positive. The local SDK 10.0.104 analyzer correctly resolves the call. Remove this suppression once Roslyn fixes the cross-block reference tracking.")]
    private static void ConfigureBrokerTransport(
        IBusRegistrationConfigurator x,
        MessageBusSettings settings,
        string? connectionString)
    {
        switch (settings.Provider)
        {
            case MessageBusProvider.RabbitMq:
                x.UsingRabbitMq((context, cfg) =>
                {
                    if (!string.IsNullOrWhiteSpace(connectionString))
                    {
                        cfg.Host(new Uri(connectionString));
                    }

                    cfg.UseMessageRetry(r => r.Exponential(
                        settings.RetryLimit,
                        TimeSpan.FromSeconds(settings.RetryMinIntervalSeconds),
                        TimeSpan.FromSeconds(settings.RetryMaxIntervalSeconds),
                        TimeSpan.FromSeconds(settings.RetryMinIntervalSeconds)));
                    cfg.ConfigureEndpoints(context);
                });
                break;

            case MessageBusProvider.AzureServiceBus:
                x.UsingAzureServiceBus((context, cfg) =>
                {
                    if (!string.IsNullOrWhiteSpace(connectionString))
                    {
                        cfg.Host(connectionString);
                    }

                    cfg.UseMessageRetry(r => r.Exponential(
                        settings.RetryLimit,
                        TimeSpan.FromSeconds(settings.RetryMinIntervalSeconds),
                        TimeSpan.FromSeconds(settings.RetryMaxIntervalSeconds),
                        TimeSpan.FromSeconds(settings.RetryMinIntervalSeconds)));
                    cfg.ConfigureEndpoints(context);
                });
                break;

            case MessageBusProvider.InProcess:
            default:
                // Caller short-circuits InProcess before reaching this method.
                break;
        }
    }
}

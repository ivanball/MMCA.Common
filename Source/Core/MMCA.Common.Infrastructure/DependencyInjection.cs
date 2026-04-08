using System.Reflection;
using MassTransit;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Infrastructure;
using MMCA.Common.Infrastructure.Auth;
using MMCA.Common.Infrastructure.Caching;
using MMCA.Common.Infrastructure.Http;
using MMCA.Common.Infrastructure.Hubs;
using MMCA.Common.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;
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

            services.AddOptions<SmtpSettings>()
                .Bind(configuration.GetSection(SmtpSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();
            services.TryAddSingleton<ISmtpSettings>(sp => sp.GetRequiredService<IOptions<SmtpSettings>>().Value);

            // Register our custom IDbContextFactory (scoped — one per request) and EF Core's
            // IDbContextFactory<T> for each provider (these are the pooled factories that create raw contexts).
            services.TryAddScoped<IDbContextFactory, DbContextFactory>();
            services.AddDbContextFactory<CosmosDbContext>();
            services.AddDbContextFactory<SqliteDbContext>();
            services.AddDbContextFactory<SQLServerDbContext>();

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

            services.AddCaching();

            services.AddOptions<OutboxSettings>()
                .Bind(configuration.GetSection(OutboxSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

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

            services.AddServices();

            return services;
        }

        /// <summary>
        /// Registers the cache service. Uses distributed cache (e.g. Redis registered by Aspire)
        /// when available; otherwise falls back to in-memory cache.
        /// </summary>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddCaching()
        {
            services.AddMemoryCache();

            services.TryAddSingleton<ICacheService>(sp =>
            {
                var distributedCache = sp.GetService<IDistributedCache>();
                if (distributedCache is not null and not MemoryDistributedCache)
                {
                    var multiplexer = sp.GetService<IConnectionMultiplexer>();
                    return new DistributedCacheService(distributedCache, multiplexer);
                }

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
            services.TryAddScoped<ITokenService, TokenService>();
            services.TryAddSingleton<IPasswordHasher, PasswordHasher>();
            services.TryAddScoped<IEventBus, InProcessEventBus>();
            services.TryAddScoped<IIntegrationEventPublisher, IntegrationEventPublisher>();

            // IMessageBus is the new abstraction used by OutboxProcessor (and, going forward, by
            // application code that publishes integration events). The default registration is
            // InProcessMessageBus — call AddBrokerMessaging(...) from a service host's Program.cs
            // to swap in MassTransit-backed BrokerMessageBus for microservice deployments.
            services.TryAddScoped<IMessageBus, InProcessMessageBus>();

            services.TryAddSingleton(TimeProvider.System);
            services.TryAddTransient<IEmailSender, SmtpEmailSender>();
            services.TryAddTransient<IPushNotificationSender, NullPushNotificationSender>();

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
        /// with <see cref="SignalRPushNotificationSender"/>. Optionally configures a Redis backplane when a Redis
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

            // Replace the default NullPushNotificationSender with the SignalR implementation.
            services.AddTransient<IPushNotificationSender, SignalRPushNotificationSender>();
            services.TryAddSingleton<IUserIdProvider, ClaimBasedUserIdProvider>();

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

            services.AddMassTransit(x =>
            {
                if (!string.IsNullOrWhiteSpace(settings.EndpointPrefix))
                {
                    x.SetKebabCaseEndpointNameFormatter();
                }

                configureConsumers?.Invoke(x);

                switch (settings.Provider)
                {
                    case MessageBusProvider.RabbitMq:
                        x.UsingRabbitMq((context, cfg) =>
                        {
                            if (!string.IsNullOrWhiteSpace(settings.ConnectionString))
                            {
                                cfg.Host(settings.ConnectionString);
                            }

                            cfg.ConfigureEndpoints(context);
                        });
                        break;

                    case MessageBusProvider.AzureServiceBus:
                        x.UsingAzureServiceBus((context, cfg) =>
                        {
                            if (!string.IsNullOrWhiteSpace(settings.ConnectionString))
                            {
                                cfg.Host(settings.ConnectionString);
                            }

                            cfg.ConfigureEndpoints(context);
                        });
                        break;

                    case MessageBusProvider.InProcess:
                    default:
                        // Already short-circuited above; no-op fallback for completeness.
                        break;
                }
            });

            // Swap the default in-process bus for the broker-backed one. Use Replace so we
            // overwrite the AddServices() registration rather than appending a second one.
            services.Replace(ServiceDescriptor.Scoped<IMessageBus, BrokerMessageBus>());

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

            var builder = services.AddHttpClient<TInterface, TImplementation>(client =>
                    client.BaseAddress = new Uri($"http://{serviceName}"))
                .AddHttpMessageHandler<JwtForwardingDelegatingHandler>();

            builder.AddStandardResilienceHandler();
            return builder;
        }
    }
}

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure;
using MMCA.Common.Infrastructure.Caching;
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

            services.TryAddSingleton(TimeProvider.System);
            services.TryAddTransient<IEmailSender, SmtpEmailSender>();

            return services;
        }
    }
}

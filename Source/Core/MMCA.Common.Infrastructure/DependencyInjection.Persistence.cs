using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using Npgsql;

namespace MMCA.Common.Infrastructure;

public static partial class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers a configurator for the <see cref="NpgsqlDataSource"/> the PostgreSQL contexts and
        /// the PostgreSQL readiness check share, and switches PostgreSQL onto that shared data source:
        /// one per distinct connection string, built on first use and disposed with the container.
        /// Adapter packages call this from their own registration method (for example
        /// <c>AddAwsRdsIamAuthentication(configuration)</c>); a host calls it directly only for a
        /// configurator of its own. Order-independent relative to <c>AddInfrastructure</c> and
        /// <c>AddServiceDefaults</c>, since both sides look the data source up when it is first needed.
        /// Idempotent per configurator type.
        /// </summary>
        /// <param name="configurator">The configurator instance.</param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddNpgsqlDataSourceConfigurator(INpgsqlDataSourceConfigurator configurator)
        {
            ArgumentNullException.ThrowIfNull(configurator);

            services.TryAddEnumerable(ServiceDescriptor.Singleton<INpgsqlDataSourceConfigurator>(configurator));

            // The factory may return null for a key that is not a connection string; the container
            // passes that through as "not resolved" (GetKeyedService returns null).
            services.TryAddKeyedSingleton<NpgsqlDataSource>(
                KeyedService.AnyKey,
                (serviceProvider, key) => NpgsqlDataSources.Create(serviceProvider, key)!);

            return services;
        }
    }
}

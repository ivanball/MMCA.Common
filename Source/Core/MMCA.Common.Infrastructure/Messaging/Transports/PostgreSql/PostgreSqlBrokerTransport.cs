using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using Npgsql;

namespace MMCA.Common.Infrastructure.Messaging.Transports.PostgreSql;

/// <summary>
/// The built-in PostgreSQL transport (<c>MessageBus:Provider=PostgreSql</c>): topics, queues and delayed
/// redeliveries kept in three tables of one PostgreSQL database (<see cref="PostgreSqlTransportSql"/>),
/// so any PostgreSQL (Cloud SQL, Amazon RDS, Azure Database for PostgreSQL, a container) is the broker,
/// which is why it is core rather than an adapter. Written on Npgsql against the same
/// <see cref="IBrokerTransport"/> contract an adapter package implements.
/// <para>
/// A publish inserts one row per subscribed queue in one statement and wakes the receivers with
/// <c>pg_notify</c>; a receiver claims rows with <c>FOR UPDATE SKIP LOCKED</c> under a renewed lease and
/// falls back to polling when no notification arrives. A redelivery pushes the row's
/// <c>visible_after</c> out, so there is no delay cap (<see cref="MaxRedeliveryDelay"/> is
/// <see langword="null"/>); a dead letter moves the row to <c>{schema}.dead_letters</c>.
/// </para>
/// <para>
/// The connection string is the shared broker one (<c>MessageBus:ConnectionString</c>, then the
/// <c>ConnectionStrings</c> fallbacks); a broker host without one fails at registration. The schema
/// (<c>MessageBus:PostgreSql:Schema</c>, default <c>transport</c>) and its owning role
/// (<c>MessageBus:PostgreSql:Role</c>, default <c>transport</c>) are created at startup ahead of the
/// receiver; <c>MessageBus:PostgreSql:MigrateOnStartup=false</c> turns that off.
/// </para>
/// <para>
/// Authentication: when an <see cref="INpgsqlDataSourceConfigurator"/> is registered (an RDS, Cloud SQL
/// or Entra token provider), the transport runs on the SAME shared <see cref="NpgsqlDataSource"/> the
/// PostgreSQL contexts use for that connection string, the startup migration included.
/// </para>
/// </summary>
internal sealed class PostgreSqlBrokerTransport : IBrokerTransport
{
    /// <inheritdoc />
    public string Name => MessageBusProviders.PostgreSql;

    /// <inheritdoc />
    public TimeSpan? MaxRedeliveryDelay => null;

    /// <inheritdoc />
    public void Register(IServiceCollection services, BrokerTransportContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);

        var connectionString = RequireConnectionString(context);
        var transportSettings = ReadSettings(context.Configuration);
        var sql = new PostgreSqlTransportSql(transportSettings.EffectiveSchema, transportSettings.EffectiveRole);
        var migrateOnStartup = transportSettings.MigrateOnStartup;
        var topology = context.Topology;

        services.AddSingleton(sp => CreateStore(sp, connectionString, sql));
        services.AddSingleton<IBrokerPublisher>(sp => new PostgreSqlPublisher(sp.GetRequiredService<PostgreSqlMessageStore>(), migrateOnStartup));

        // Hosted services start in registration order: the migrator finishes before the receiver starts.
        if (migrateOnStartup)
        {
            services.AddHostedService(sp => new PostgreSqlSchemaMigrator(sp.GetRequiredService<PostgreSqlMessageStore>()));
        }

        if (topology.Subscriptions.Count == 0)
        {
            return;
        }

        var options = PostgreSqlReceiverOptions.From(context.Settings, migrateOnStartup);
        services.AddHostedService(sp => new PostgreSqlReceiver(
            topology,
            sp.GetRequiredService<IBrokerReceivePipeline>(),
            sp.GetRequiredService<PostgreSqlMessageStore>(),
            options,
            sp.GetRequiredService<ILogger<PostgreSqlReceiver>>()));
    }

    /// <summary>Binds <c>MessageBus:PostgreSql</c>.</summary>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The transport settings, defaults when the section is absent.</returns>
    internal static PostgreSqlTransportSettings ReadSettings(IConfiguration configuration) =>
        configuration.GetSection(PostgreSqlTransportSettings.SectionPath).Get<PostgreSqlTransportSettings>()
            ?? new PostgreSqlTransportSettings();

    private static PostgreSqlMessageStore CreateStore(IServiceProvider serviceProvider, string connectionString, PostgreSqlTransportSql sql)
    {
        var shared = NpgsqlDataSources.Resolve(serviceProvider, connectionString);
        return shared is not null
            ? new PostgreSqlMessageStore(shared, ownsDataSource: false, sql)
            : new PostgreSqlMessageStore(NpgsqlDataSource.Create(connectionString), ownsDataSource: true, sql);
    }

    private static string RequireConnectionString(BrokerTransportContext context) =>
        string.IsNullOrWhiteSpace(context.ConnectionString)
            ? throw new InvalidOperationException(
                $"MessageBus:Provider is '{MessageBusProviders.PostgreSql}', but no broker connection string is configured. "
                + "Set MessageBus:ConnectionString (or ConnectionStrings:messaging) to the PostgreSQL connection string of the broker database; "
                + "unlike RabbitMQ there is no default host to fall back to.")
            : context.ConnectionString;
}

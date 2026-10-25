using MassTransit;
using MassTransit.SqlTransport.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Infrastructure.Persistence.DbContexts;

namespace MMCA.Common.Infrastructure.Messaging.Transports;

/// <summary>
/// The built-in PostgreSQL transport (<c>MessageBus:Provider=PostgreSql</c>): MassTransit's SQL
/// transport, which keeps queues, topics, subscriptions and scheduled messages in tables and functions
/// of one PostgreSQL database. Any PostgreSQL is the broker (Cloud SQL, Amazon RDS, Azure Database for
/// PostgreSQL, a container), which is why it is core rather than an adapter. Written against the same
/// <see cref="IBrokerTransport"/> contract an adapter package implements.
/// <para>
/// The connection string is the shared broker one (<c>MessageBus:ConnectionString</c>, then the
/// <c>ConnectionStrings</c> fallbacks); a broker host without one fails at registration. The transport
/// schema (<c>MessageBus:PostgreSql:Schema</c>, default <c>transport</c>) and role
/// (<c>MessageBus:PostgreSql:Role</c>, default <c>transport</c>) are created or updated at startup by
/// MassTransit's migration hosted service, registered ahead of the bus so the tables exist before any
/// endpoint starts; <c>MessageBus:PostgreSql:MigrateOnStartup=false</c> turns that off.
/// </para>
/// <para>
/// Second-level delayed redelivery is applied UNCONDITIONALLY, as on Azure Service Bus and Amazon SQS:
/// the SQL transport delays a message natively (a visibility time on its row), so there is no plugin to
/// install and <c>MessageBus:EnableDelayedRedelivery</c>, which exists only to gate the RabbitMQ plugin,
/// is not consulted. <c>MessageBus:RedeliveryIntervalsSeconds=[0]</c> turns it off.
/// </para>
/// <para>
/// Authentication: when an <see cref="INpgsqlDataSourceConfigurator"/> is registered (an RDS or Cloud
/// SQL IAM token provider, for example), the bus runs on the SAME shared
/// <c>NpgsqlDataSource</c> the PostgreSQL contexts use for that connection string, so the configurator
/// applies to the broker's connections too. The startup migration cannot: MassTransit's migrator opens
/// its own connections from the connection string, so it authenticates with the password in that
/// string. A broker database on IAM authentication therefore sets
/// <c>MessageBus:PostgreSql:MigrateOnStartup=false</c> and creates the transport schema from a
/// deployment step that connects with a password.
/// </para>
/// </summary>
internal sealed class PostgreSqlBrokerTransport : IBrokerTransport
{
    /// <inheritdoc />
    public string Name => MessageBusProviders.PostgreSql;

    /// <summary>
    /// Registers what must exist BEFORE <c>AddMassTransit</c> runs: MassTransit's migration hosted
    /// service, which has to precede the bus hosted service <c>AddMassTransit</c> registers first
    /// (hosted services start in registration order), configured with the broker connection string,
    /// schema and role. Also fails a missing connection string before any bus registration.
    /// </summary>
    /// <param name="services">The service collection being configured.</param>
    /// <param name="context">The bound settings, the resolved connection string and the configuration.</param>
    /// <exception cref="InvalidOperationException">No broker connection string is configured.</exception>
    internal static void RegisterServices(IServiceCollection services, BrokerTransportContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);

        var connectionString = RequireConnectionString(context);
        var transportSettings = ReadSettings(context.Configuration);
        if (!transportSettings.MigrateOnStartup)
        {
            return;
        }

        services.AddOptions<SqlTransportOptions>().Configure(options =>
        {
            options.ConnectionString = connectionString;
            options.Schema = transportSettings.EffectiveSchema;
            options.Role = transportSettings.EffectiveRole;
        });

        // MassTransit's own defaults: create the database when it is missing, then the schema, the
        // role grants and the transport tables and functions (all idempotent); never delete.
        services.AddPostgresMigrationHostedService(create: true, delete: false);
    }

    /// <inheritdoc />
    public void Configure(IBusRegistrationConfigurator configurator, BrokerTransportContext context)
    {
        ArgumentNullException.ThrowIfNull(configurator);
        ArgumentNullException.ThrowIfNull(context);

        var settings = context.Settings;
        var connectionString = RequireConnectionString(context);
        var schema = ReadSettings(context.Configuration).EffectiveSchema;

        // The registration bus factory UsingPostgres wraps, used directly so the host callback can set
        // the schema on whichever host overload the data-source decision picks.
        configurator.SetBusFactory(new SqlRegistrationBusFactory((busContext, cfg) =>
        {
            // The container's own provider, which resolves keyed services; the registration context
            // only forwards plain lookups. Null when no configurator is registered.
            var dataSource = NpgsqlDataSources.Resolve(busContext.GetRequiredService<IServiceProvider>(), connectionString);
            if (dataSource is not null)
            {
                cfg.UsePostgres(dataSource, host => host.Schema = schema);
            }
            else
            {
                cfg.UsePostgres(connectionString, host => host.Schema = schema);
            }

            BrokerTransportPolicies.ApplyReceivePolicies(cfg, settings, applyDelayedRedelivery: true);
            cfg.ConfigureEndpoints(busContext);
        }));
    }

    private static PostgreSqlTransportSettings ReadSettings(IConfiguration configuration) =>
        configuration.GetSection(PostgreSqlTransportSettings.SectionPath).Get<PostgreSqlTransportSettings>()
            ?? new PostgreSqlTransportSettings();

    private static string RequireConnectionString(BrokerTransportContext context) =>
        string.IsNullOrWhiteSpace(context.ConnectionString)
            ? throw new InvalidOperationException(
                $"MessageBus:Provider is '{MessageBusProviders.PostgreSql}', but no broker connection string is configured. "
                + "Set MessageBus:ConnectionString (or ConnectionStrings:messaging) to the PostgreSQL connection string of the broker database; "
                + "unlike RabbitMQ there is no default host to fall back to.")
            : context.ConnectionString;
}

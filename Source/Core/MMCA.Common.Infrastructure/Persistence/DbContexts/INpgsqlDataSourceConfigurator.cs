using Npgsql;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts;

/// <summary>
/// Configures the <see cref="NpgsqlDataSource"/> every PostgreSQL context and the PostgreSQL readiness
/// check share for one connection string: a password provider (an RDS IAM token, a vault lease), a
/// client certificate callback, a type mapping. Register one with
/// <c>services.AddNpgsqlDataSourceConfigurator(...)</c>; an adapter package does that from its own
/// registration call (for example <c>AddAwsRdsIamAuthentication(configuration)</c>).
/// <para>
/// With none registered nothing changes: <see cref="PostgreSQLDbContext"/> hands EF the raw
/// connection string exactly as before and no data source is built. With at least one, ONE data source
/// is built per distinct connection string, the first time a context or the health check asks for it,
/// every registered configurator runs on its builder once, and the container disposes it.
/// </para>
/// </summary>
/// <remarks>
/// Applies to PostgreSQL connection strings only; SQL Server, SQLite and Cosmos sources never reach a
/// configurator. Throwing from <see cref="Configure"/> fails the first context (or probe) for that
/// connection string, which is the place to reject a connection string a configurator cannot serve.
/// </remarks>
public interface INpgsqlDataSourceConfigurator
{
    /// <summary>Applies this configurator's settings to the builder for one connection string.</summary>
    /// <param name="builder">The builder, already carrying the connection string (see <see cref="NpgsqlDataSourceBuilder.ConnectionStringBuilder"/>).</param>
    void Configure(NpgsqlDataSourceBuilder builder);
}

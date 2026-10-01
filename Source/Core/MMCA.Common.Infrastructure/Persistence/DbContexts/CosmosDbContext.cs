using Microsoft.EntityFrameworkCore;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.InternalCommands;
using MMCA.Common.Infrastructure.Persistence.Outbox;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts;

/// <summary>
/// DbContext targeting Azure Cosmos DB. One instance exists per physical Cosmos data source
/// (account + database); connection string and database name come from the resolved
/// <see cref="PhysicalDataSource"/>. Automatically detects the local emulator
/// and adjusts connection mode and SSL settings accordingly.
/// </summary>
public sealed class CosmosDbContext(
    DbContextOptions<CosmosDbContext> options,
    IServiceProvider serviceProvider,
    IEntityConfigurationAssemblyProvider assemblyProvider,
    PhysicalDataSource physicalDataSource)
    : ApplicationDbContext(options, serviceProvider, assemblyProvider, physicalDataSource)
{
    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        var connectionString = PhysicalSource.ConnectionString;

        // Two decisions, deliberately separate. The emulator key alone selects Gateway mode (as it
        // always has). Disabling certificate validation additionally requires a loopback endpoint,
        // because the key is public and so never proves the endpoint is the local emulator.
        var usesEmulatorKey = UsesEmulatorKey(connectionString);
        var bypassCertificateValidation = ShouldBypassCertificateValidation(connectionString);

        optionsBuilder
            .UseCosmos(
                connectionString: connectionString,
                databaseName: PhysicalSource.CosmosDatabaseName,
                cosmosOptionsAction: options =>
                {
                    if (usesEmulatorKey)
                    {
                        // Emulator: Gateway mode required because Direct mode fails with self-signed certs.
                        options.ConnectionMode(Microsoft.Azure.Cosmos.ConnectionMode.Gateway);

                        if (bypassCertificateValidation)
                        {
                            // SSL validation is intentionally bypassed for the emulator's self-signed
                            // certificate, and only on a loopback endpoint.
                            options.HttpClientFactory(() =>
                            {
#pragma warning disable S4830 // Server certificate validation: emulator uses self-signed cert
                                var handler = new HttpClientHandler
                                {
                                    ServerCertificateCustomValidationCallback =
                                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                                };
#pragma warning restore S4830
                                return new HttpClient(handler);
                            });
                        }
                    }
                    else
                    {
                        // Production: Direct mode for lower latency; tune connection pool limits.
                        options.ConnectionMode(Microsoft.Azure.Cosmos.ConnectionMode.Direct);
                        options.MaxRequestsPerTcpConnection(20);
                        options.MaxTcpConnectionsPerEndpoint(32);
                    }
                });

        base.OnConfiguring(optionsBuilder);
    }

    /// <summary>
    /// Whether the connection string carries the Cosmos DB Emulator's well-known account key, which
    /// selects Gateway mode.
    /// </summary>
    /// <param name="connectionString">The physical source's Cosmos connection string.</param>
    /// <returns><see langword="true"/> when the emulator key is present.</returns>
    /// <remarks>"C2y6yDjf5" is the well-known prefix of the Cosmos DB Emulator's default account key.</remarks>
    internal static bool UsesEmulatorKey(string connectionString) =>
        connectionString.Contains("C2y6yDjf5", StringComparison.Ordinal);

    /// <summary>
    /// Whether TLS certificate validation is disabled for the emulator's self-signed certificate:
    /// only when the emulator key is present AND the <c>AccountEndpoint</c> host is loopback
    /// (<c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>). The key is published by Microsoft, so on its
    /// own it never proves the endpoint is the local emulator.
    /// </summary>
    /// <param name="connectionString">The physical source's Cosmos connection string.</param>
    /// <returns><see langword="true"/> when certificate validation may be bypassed.</returns>
    /// <remarks>
    /// The endpoint is parsed as a <see cref="Uri"/> and judged by <see cref="Uri.IsLoopback"/>, never
    /// by a string prefix, so a host such as <c>localhost.attacker.example</c> does not qualify. An
    /// unparseable connection string or endpoint keeps validation on.
    /// </remarks>
    internal static bool ShouldBypassCertificateValidation(string connectionString) =>
        UsesEmulatorKey(connectionString) && HasLoopbackAccountEndpoint(connectionString);

    private static bool HasLoopbackAccountEndpoint(string connectionString)
    {
        var builder = new System.Data.Common.DbConnectionStringBuilder();
        try
        {
            builder.ConnectionString = connectionString;
        }
        catch (ArgumentException)
        {
            return false;
        }

        return builder.TryGetValue("AccountEndpoint", out var endpoint)
            && Uri.TryCreate(endpoint as string, UriKind.Absolute, out var uri)
            && uri.IsLoopback;
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ApplyConfigurationsForEntitiesInContext(DataSource.CosmosDB, modelBuilder);

        // Cosmos does not support the outbox table (relational-only).
        modelBuilder.Ignore<OutboxMessage>();

        // Nor the internal-command job queue, for the same reason.
        modelBuilder.Ignore<InternalCommandMessage>();

        // Strip relational-specific indexes (e.g. HasIndex / HasFilter) that the
        // Cosmos provider does not support. This allows entity configurations to
        // share the same body across SQL Server and Cosmos — the provider-specific
        // base class handles all differences.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var index in entityType.GetIndexes().ToList())
                entityType.RemoveIndex(index);
        }

        // Does NOT call base.OnModelCreating because every table the base maps (outbox, inbox,
        // internal commands, scheduler, audit trail, refresh sessions) is a relational-only
        // construct the Cosmos provider cannot express. Soft-delete and tenant filters are applied
        // independently via the extracted helper methods; the tenant helper skips its index for
        // Cosmos, which indexes every property itself.
        ApplySoftDeleteFilters(modelBuilder);
        ApplyTenantFilters(modelBuilder);
    }
}

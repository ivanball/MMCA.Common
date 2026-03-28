using Microsoft.EntityFrameworkCore;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts;

/// <summary>
/// DbContext targeting Azure Cosmos DB. Automatically detects the local emulator
/// and adjusts connection mode and SSL settings accordingly.
/// </summary>
public sealed class CosmosDbContext(
    DbContextOptions<CosmosDbContext> options,
    IServiceProvider serviceProvider,
    IConnectionStringSettings connectionStringSettings,
    IEntityConfigurationAssemblyProvider assemblyProvider)
    : ApplicationDbContext(options, serviceProvider, assemblyProvider)
{
    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        var connectionString = connectionStringSettings.CosmosConnectionString;

        // "C2y6yDjf5" is the well-known prefix of the Cosmos DB Emulator's default account key.
        var isEmulator = connectionString.Contains("C2y6yDjf5", StringComparison.Ordinal);

        optionsBuilder
            .UseCosmos(
                connectionString: connectionString,
                databaseName: "AtlDevCon",
                cosmosOptionsAction: options =>
                {
                    if (isEmulator)
                    {
                        // Emulator: Gateway mode required because Direct mode fails with self-signed certs.
                        // SSL validation is intentionally bypassed — safe only in local dev environments.
                        options.ConnectionMode(Microsoft.Azure.Cosmos.ConnectionMode.Gateway);
                        options.HttpClientFactory(() =>
                        {
#pragma warning disable S4830 // Server certificate validation — emulator uses self-signed cert
                            var handler = new HttpClientHandler
                            {
                                ServerCertificateCustomValidationCallback =
                                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                            };
#pragma warning restore S4830
                            return new HttpClient(handler);
                        });
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
    /// Cosmos DB does not support relational outbox tables; events are dispatched in-process only.
    /// </summary>
    internal override bool SupportsOutbox => false;

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ApplyConfigurationsForEntitiesInContext(DataSource.CosmosDB, modelBuilder);

        // Cosmos does not support the outbox table (relational-only).
        modelBuilder.Ignore<OutboxMessage>();

        // Strip relational-specific indexes (e.g. HasIndex / HasFilter) that the
        // Cosmos provider does not support. This allows entity configurations to
        // share the same body across SQL Server and Cosmos — the provider-specific
        // base class handles all differences.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var index in entityType.GetIndexes().ToList())
                entityType.RemoveIndex(index);
        }

        // Does NOT call base.OnModelCreating because the base also registers
        // ValReturn<T> keyless types mapped to views (a relational-only construct that
        // the Cosmos provider does not support). Soft-delete filters are applied
        // independently via the extracted helper method.
        ApplySoftDeleteFilters(modelBuilder);
    }
}

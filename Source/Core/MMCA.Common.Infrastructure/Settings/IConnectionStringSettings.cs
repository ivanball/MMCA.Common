namespace MMCA.Common.Infrastructure.Settings;

/// <summary>
/// Connection strings for all supported data sources. Bound from the <c>ConnectionStrings</c> configuration section.
/// </summary>
public interface IConnectionStringSettings
{
    /// <summary>Gets the Azure Cosmos DB connection string.</summary>
    string CosmosConnectionString { get; init; }

    /// <summary>Gets the SQLite connection string (typically a file path).</summary>
    string SqliteConnectionString { get; init; }

    /// <summary>Gets the SQL Server connection string.</summary>
    string SQLServerConnectionString { get; init; }

    /// <summary>
    /// Gets the assembly name containing EF Core migrations for SQL Server.
    /// When empty, EF defaults to the DbContext assembly.
    /// </summary>
    string SQLServerMigrationsAssembly { get; init; }
}

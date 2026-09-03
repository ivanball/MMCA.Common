namespace MMCA.Common.Infrastructure.Persistence.DataSources;

/// <summary>
/// Connection strings bound from the <c>ConnectionStrings</c> configuration section.
/// <para>
/// No single property is required. SQL Server is the default engine, but a host may run entirely on
/// SQLite or Cosmos, and may declare its databases through the <c>DataSources</c> section instead of
/// this one. What IS required is that the host can reach some database:
/// <see cref="ConnectionStringSettingsValidator"/> enforces that across both sections at startup.
/// </para>
/// </summary>
public sealed class ConnectionStringSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "ConnectionStrings";

    /// <summary>Gets the Azure Cosmos DB connection string.</summary>
    public string CosmosConnectionString { get; init; } = string.Empty;

    /// <summary>Gets the Cosmos DB database name for the default source.</summary>
    public string CosmosDatabaseName { get; init; } = "AtlDevCon";

    /// <summary>Gets the SQLite connection string (typically a file path).</summary>
    public string SqliteConnectionString { get; init; } = string.Empty;

    /// <summary>Gets the SQL Server connection string.</summary>
    public string SQLServerConnectionString { get; init; } = string.Empty;

    /// <summary>
    /// Gets the assembly name containing EF Core migrations for SQL Server.
    /// When empty, EF defaults to the DbContext assembly.
    /// </summary>
    public string SQLServerMigrationsAssembly { get; init; } = string.Empty;
}

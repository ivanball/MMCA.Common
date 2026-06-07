namespace MMCA.Common.Infrastructure.Settings;

/// <summary>
/// Connection settings for one named (logical) data source under the <c>DataSources</c>
/// configuration section. All properties are optional — any value left empty falls back to the
/// corresponding top-level <c>ConnectionStrings</c> value, which collapses the logical name onto
/// the <c>Default</c> physical source (preserving single-database behavior).
/// </summary>
/// <example>
/// <code language="json">
/// "DataSources": {
///   "Conference": {
///     "SQLServerConnectionString": "Server=localhost;Database=ADC_Conference;...",
///     "SQLServerMigrationsAssembly": "MMCA.ADC.Migrations.SqlServer.Conference"
///   }
/// }
/// </code>
/// </example>
public sealed class DataSourceEntrySettings
{
    /// <summary>Gets the Azure Cosmos DB connection string for this source.</summary>
    public string CosmosConnectionString { get; init; } = string.Empty;

    /// <summary>Gets the Cosmos DB database name for this source. Falls back to the top-level name.</summary>
    public string CosmosDatabaseName { get; init; } = string.Empty;

    /// <summary>Gets the SQLite connection string for this source.</summary>
    public string SqliteConnectionString { get; init; } = string.Empty;

    /// <summary>Gets the SQL Server connection string for this source.</summary>
    public string SQLServerConnectionString { get; init; } = string.Empty;

    /// <summary>
    /// Gets the assembly containing EF Core migrations for this source's SQL Server database.
    /// Falls back to the top-level <c>SQLServerMigrationsAssembly</c> when empty.
    /// </summary>
    public string SQLServerMigrationsAssembly { get; init; } = string.Empty;
}

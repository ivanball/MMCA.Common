namespace MMCA.Common.Infrastructure.Settings;

/// <summary>
/// Concrete settings bound from the <c>ConnectionStrings</c> configuration section.
/// <para>
/// No single property is required. SQL Server is the default engine, but a host may run entirely on
/// SQLite or Cosmos, and may declare its databases through the <c>DataSources</c> section instead of
/// this one. What IS required is that the host can reach some database:
/// <see cref="ConnectionStringSettingsValidator"/> enforces that across both sections at startup.
/// </para>
/// </summary>
public sealed class ConnectionStringSettings : IConnectionStringSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "ConnectionStrings";

    /// <inheritdoc />
    public string CosmosConnectionString { get; init; } = string.Empty;

    /// <inheritdoc />
    public string CosmosDatabaseName { get; init; } = "AtlDevCon";

    /// <inheritdoc />
    public string SqliteConnectionString { get; init; } = string.Empty;

    /// <inheritdoc />
    public string SQLServerConnectionString { get; init; } = string.Empty;

    /// <inheritdoc />
    public string SQLServerMigrationsAssembly { get; init; } = string.Empty;
}

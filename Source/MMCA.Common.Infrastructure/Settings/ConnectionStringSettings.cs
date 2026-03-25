using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.Infrastructure.Settings;

/// <summary>
/// Concrete settings bound from the <c>ConnectionStrings</c> configuration section.
/// Only <see cref="SQLServerConnectionString"/> is required because SQL Server is the default data source.
/// </summary>
public sealed class ConnectionStringSettings : IConnectionStringSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "ConnectionStrings";

    /// <inheritdoc />
    public string CosmosConnectionString { get; init; } = string.Empty;

    /// <inheritdoc />
    public string SqliteConnectionString { get; init; } = string.Empty;

    /// <inheritdoc />
    [Required]
    public string SQLServerConnectionString { get; init; } = string.Empty;

    /// <inheritdoc />
    public string SQLServerMigrationsAssembly { get; init; } = string.Empty;
}

using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.Infrastructure.Persistence.DataSources;

/// <summary>
/// Fully resolved connection information for one physical data source (one database).
/// Produced by <see cref="IDataSourceResolver"/> from the top-level <c>ConnectionStrings</c>
/// section (the <c>Default</c> source) and the named <c>DataSources</c> entries.
/// </summary>
/// <param name="Key">The physical identity (engine + name) of this source.</param>
/// <param name="ConnectionString">The engine-specific connection string.</param>
/// <param name="SqlServerMigrationsAssembly">
/// The EF Core migrations assembly for SQL Server sources; <see langword="null"/> when EF should
/// default to the context assembly. Ignored for non-SQL-Server engines.
/// </param>
/// <param name="CosmosDatabaseName">The Cosmos DB database name. Ignored for relational engines.</param>
public sealed record PhysicalDataSource(
    DataSourceKey Key,
    string ConnectionString,
    string? SqlServerMigrationsAssembly,
    string CosmosDatabaseName);

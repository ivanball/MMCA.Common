using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;

namespace MMCA.Common.Infrastructure.Tests.TestDoubles;

/// <summary>
/// An <see cref="IEntityDataSourceRegistry"/> with no registered entities. With an empty registry
/// the cross-source degrade convention no-ops during model building, preserving the legacy
/// single-database model behavior for test contexts.
/// </summary>
internal sealed class EmptyEntityDataSourceRegistry : IEntityDataSourceRegistry
{
    public DataSourceKey GetDataSourceKey(Type entityType) =>
        throw new InvalidOperationException($"No data source registered for entity type \"{entityType}\".");

    public DataSourceKey GetDataSourceKey(string entityFullName) =>
        throw new InvalidOperationException($"No data source registered for entity \"{entityFullName}\".");

    public bool TryGetDataSourceKey(string entityFullName, out DataSourceKey key)
    {
        key = default;
        return false;
    }

    public IReadOnlyCollection<DataSourceKey> GetPhysicalSourcesInUse() => [];
}

/// <summary>
/// Ready-made <see cref="PhysicalDataSource"/> values for constructing
/// <c>ApplicationDbContext</c>-derived test contexts.
/// </summary>
internal static class TestPhysicalDataSources
{
    public static PhysicalDataSource Sqlite(string connectionString = "DataSource=:memory:") =>
        new(DataSourceKey.Default(DataSource.Sqlite), connectionString, null, "AtlDevCon");

    public static PhysicalDataSource SqlServer(string connectionString = "Server=test;Database=test") =>
        new(DataSourceKey.Default(DataSource.SQLServer), connectionString, null, "AtlDevCon");

    public static PhysicalDataSource Cosmos(string connectionString = "AccountEndpoint=https://test;AccountKey=dGVzdA==") =>
        new(DataSourceKey.Default(DataSource.CosmosDB), connectionString, null, "AtlDevCon");
}

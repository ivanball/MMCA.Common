using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.Infrastructure;

/// <summary>
/// Marks an entity type configuration class with the <see cref="DataSource"/> it targets.
/// Read by <see cref="Services.DataSourceService"/> at model-building time to populate
/// the entity-to-data-source mapping cache, enabling <see cref="Persistence.UnitOfWork"/>
/// to route each entity to the correct <see cref="Persistence.DbContexts.ApplicationDbContext"/>.
/// </summary>
/// <param name="dataSource">The data source this configuration targets.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class UseDataSourceAttribute(DataSource dataSource) : Attribute
{
    /// <summary>Gets the target data source.</summary>
    public DataSource DataSource { get; } = dataSource;
}

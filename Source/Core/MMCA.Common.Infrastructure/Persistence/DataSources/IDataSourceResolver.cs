using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.Infrastructure.Persistence.DataSources;

/// <summary>
/// Maps <b>logical</b> data source names (from <c>[UseDatabase]</c> attributes, module namespaces,
/// or settings like <c>Outbox:DatabaseName</c>) to <b>physical</b> data sources, collapsing logical
/// names that point at the same database onto one <see cref="DataSourceKey"/>.
/// <para>
/// The collapse is the backward-compatibility guarantee: in a host with no <c>DataSources</c>
/// configuration, every logical name resolves to <c>Default</c>, yielding a single DbContext per
/// engine — identical change tracker, FK constraints, transactions, and EF model as before.
/// </para>
/// </summary>
public interface IDataSourceResolver
{
    /// <summary>
    /// Resolves a logical name to its physical data source key for the given engine.
    /// Logical names without a <c>DataSources</c> entry, without a connection string for the
    /// engine, or whose connection equals the top-level one collapse to
    /// <see cref="DataSourceKey.Default(DataSource)"/>. Entries sharing a connection with each
    /// other collapse to one physical source named after the alphabetically-first logical name.
    /// </summary>
    /// <param name="engine">The database engine.</param>
    /// <param name="logicalName">The logical data source name.</param>
    /// <returns>The physical data source key.</returns>
    DataSourceKey ResolveLogical(DataSource engine, string logicalName);

    /// <summary>
    /// Gets the resolved connection information for a physical data source key.
    /// </summary>
    /// <param name="key">The physical key (must come from <see cref="ResolveLogical"/> or be a Default key).</param>
    /// <returns>The physical data source connection information.</returns>
    /// <exception cref="InvalidOperationException">The key does not identify a configured physical source.</exception>
    PhysicalDataSource GetPhysical(DataSourceKey key);
}

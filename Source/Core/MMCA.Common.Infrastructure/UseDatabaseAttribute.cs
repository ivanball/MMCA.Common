namespace MMCA.Common.Infrastructure;

/// <summary>
/// Declares the <b>logical data source name</b> (database) an entity type configuration targets,
/// enabling "database per microservice" routing. Distinct from <see cref="UseDataSourceAttribute"/>,
/// which declares the database <em>engine</em> (SQL Server / Cosmos / SQLite) on the provider-specific
/// configuration base classes — this attribute selects <em>which database on that engine</em>.
/// <para>
/// Resolution order for an entity's logical name:
/// <list type="number">
///   <item>This attribute on the concrete configuration class (inherited).</item>
///   <item>The module name derived from the entity namespace (segment before <c>Domain</c>).</item>
///   <item><c>"Default"</c> — the top-level <c>ConnectionStrings</c> section.</item>
/// </list>
/// The logical name maps to connection strings via the <c>DataSources</c> configuration section;
/// names without an entry (or whose connection string equals the top-level one) collapse onto the
/// <c>Default</c> physical source, preserving single-database behavior.
/// </para>
/// </summary>
/// <param name="name">The logical data source name (e.g. <c>"Conference"</c>).</param>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class UseDatabaseAttribute(string name) : Attribute
{
    /// <summary>Gets the logical data source name.</summary>
    public string Name { get; } = name;
}

using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.DataSources.Engines;
using MMCA.Common.Infrastructure.Persistence.Tenancy;

namespace MMCA.Common.Infrastructure.Persistence.DataSources;

/// <summary>
/// Decides which physical databases a sweep over the framework's own relational tables
/// (<c>OutboxMessages</c>, <c>InternalCommands</c>, the audit trail) has to visit on this host: every
/// relational source backing a registered entity, plus the source the caller's settings name as its
/// table's home, expanded once per tenant that keeps its own copy of a source.
/// </summary>
/// <remarks>
/// <para>
/// The outbox processor, the internal-command processor, both cleanup services, both operator
/// surfaces and the audit-trail sweep all ask this one question, so the answer lives here rather than
/// being re-derived from the registry, the resolver and the tenancy settings inside each of them.
/// </para>
/// <para>
/// Recomputed per call by every caller: cheap, and tolerant of module assemblies loading after
/// startup. Cosmos sources are skipped because they host none of these tables.
/// </para>
/// </remarks>
/// <param name="entityDataSourceRegistry">Registry enumerating the physical data sources in use.</param>
/// <param name="dataSourceResolver">Resolves the caller's configured home source to a physical one.</param>
/// <param name="tenancyOptions">
/// Bound tenancy settings, used only to discover tenants that keep their own copy of a source: each
/// such database has its own copy of the table that nothing else would reach. Defaulted, so a host
/// without tenancy visits the shared databases only.
/// </param>
public sealed class FrameworkTableTargets(
    IEntityDataSourceRegistry entityDataSourceRegistry,
    IDataSourceResolver dataSourceResolver,
    IOptions<TenancySettings>? tenancyOptions = null)
{
    /// <summary>
    /// The targets a sweep visits when its table has no configured home of its own: every
    /// relational physical source backing a registered entity, deduplicated, then expanded per tenant
    /// that keeps its own copy.
    /// </summary>
    /// <returns>The targets to sweep, in a deterministic order.</returns>
    public IReadOnlyList<TenantDataSourceTarget> Relational() =>
        TenantDataSourceTargets.Expand(RelationalSourcesInUse().Distinct(), tenancyOptions?.Value);

    /// <summary>
    /// As <see cref="Relational()"/>, plus the source the caller's own settings name as its table's
    /// home (the outbox publish target, the internal-command scheduling target), unless that
    /// setting names an engine that hosts no framework tables.
    /// </summary>
    /// <param name="homeEngine">The engine the caller's settings name.</param>
    /// <param name="homeDatabaseName">The logical database name the caller's settings name.</param>
    /// <returns>The targets to sweep, in a deterministic order.</returns>
    public IReadOnlyList<TenantDataSourceTarget> Relational(DataSource homeEngine, string homeDatabaseName)
    {
        var sources = RelationalSourcesInUse();

        if (DataSourceEngines.For(homeEngine).Capabilities.IsRelational)
        {
            sources = sources.Append(dataSourceResolver.ResolveLogical(homeEngine, homeDatabaseName));
        }

        return TenantDataSourceTargets.Expand(sources.Distinct(), tenancyOptions?.Value);
    }

    /// <summary>
    /// As <see cref="Relational(DataSource, string)"/>, optionally narrowed to the one target an
    /// operator named. The name is matched case-insensitively against the target's display form
    /// (<see cref="TenantDataSourceTarget.ToString"/>), which is the name the operator surfaces report.
    /// </summary>
    /// <param name="homeEngine">The engine the caller's settings name.</param>
    /// <param name="homeDatabaseName">The logical database name the caller's settings name.</param>
    /// <param name="targetName">The target to keep, or <see langword="null"/> to keep every target.</param>
    /// <returns>The matching targets; empty when the name matches nothing this host owns.</returns>
    public IReadOnlyList<TenantDataSourceTarget> Named(DataSource homeEngine, string homeDatabaseName, string? targetName)
    {
        var targets = Relational(homeEngine, homeDatabaseName);

        return targetName is null
            ? targets
            : [.. targets.Where(t => string.Equals(t.ToString(), targetName, StringComparison.OrdinalIgnoreCase))];
    }

    private IEnumerable<DataSourceKey> RelationalSourcesInUse() =>
        entityDataSourceRegistry.GetPhysicalSourcesInUse()
            .Where(static key => DataSourceEngines.For(key.Engine).Capabilities.IsRelational);
}

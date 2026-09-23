using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.Tenancy;

namespace MMCA.Common.Infrastructure.Persistence.DataSources;

/// <summary>
/// One unit of work for a background sweep: a physical data source, optionally viewed as one
/// tenant. <see cref="TenantId"/> is <see langword="null"/> for the shared database and set for a
/// tenant that keeps its own copy of the source.
/// </summary>
/// <param name="Source">The physical data source to work against.</param>
/// <param name="TenantId">The tenant whose own database to work against, or null for the shared one.</param>
public readonly record struct TenantDataSourceTarget(DataSourceKey Source, string? TenantId)
{
    /// <summary>A log-friendly rendering that names the tenant when there is one.</summary>
    /// <returns>The source name, with the tenant appended for a per-tenant target.</returns>
    public override string ToString() =>
        TenantId is null
            ? Source.ToString()
            : string.Concat(Source.ToString(), " (tenant ", TenantId, ")");
}

/// <summary>
/// Expands the physical data sources a background service owns into the targets it must actually
/// visit once database-per-tenant is in play.
/// </summary>
/// <remarks>
/// <para>
/// A shared-schema tenant needs nothing here: its rows live in the shared database, which the
/// null-tenant target already drains, and the outbox has no tenant column precisely so adopting
/// tenancy never forces a migration on a consumer.
/// </para>
/// <para>
/// A tenant with its own database is invisible to the shared sweep: its outbox rows, and its trail
/// rows, are in a database nothing else opens. So each such tenant contributes one extra target per
/// source it overrides, and the sweep sets the tenant on its scope before asking for the context,
/// which is what routes the context at that tenant's connection string.
/// </para>
/// </remarks>
public static class TenantDataSourceTargets
{
    /// <summary>
    /// Produces the shared target for every source, followed by one target per (tenant, overridden
    /// source) pair.
    /// </summary>
    /// <param name="sources">The physical sources the caller owns.</param>
    /// <param name="settings">Bound tenancy settings, or null when tenancy was never registered.</param>
    /// <returns>The targets to sweep, in a deterministic order.</returns>
    public static List<TenantDataSourceTarget> Expand(
        IEnumerable<DataSourceKey> sources,
        TenancySettings? settings)
    {
        var sourceList = sources as IReadOnlyCollection<DataSourceKey> ?? [.. sources];
        var targets = new List<TenantDataSourceTarget>(sourceList.Count);

        foreach (var source in sourceList)
        {
            targets.Add(new TenantDataSourceTarget(source, null));
        }

        if (settings is null || settings.Tenants.Count == 0)
        {
            return targets;
        }

        foreach (var (tenantId, tenant) in settings.Tenants)
        {
            foreach (var source in sourceList)
            {
                if (tenant.DataSources.TryGetValue(source.Name, out var over)
                    && !string.IsNullOrWhiteSpace(TenancySettingsValidator.ConnectionStringFor(source.Engine, over)))
                {
                    targets.Add(new TenantDataSourceTarget(source, tenantId));
                }
            }
        }

        return targets;
    }

    /// <summary>
    /// The targets a sweep over the framework's own relational tables visits: every relational
    /// physical source backing a registered entity (Cosmos has none of these tables), deduplicated,
    /// then expanded per tenant that keeps its own copy.
    /// </summary>
    /// <param name="registry">The entity-to-source registry.</param>
    /// <param name="tenancy">Bound tenancy settings, or null when tenancy was never registered.</param>
    /// <returns>The targets to sweep, in a deterministic order.</returns>
    /// <remarks>Recomputed per call by every caller: cheap, and tolerant of module assemblies loading after startup.</remarks>
    internal static List<TenantDataSourceTarget> ExpandRelational(
        IEntityDataSourceRegistry registry,
        TenancySettings? tenancy) =>
        Expand(RelationalSourcesInUse(registry).Distinct(), tenancy);

    /// <summary>
    /// As <see cref="ExpandRelational(IEntityDataSourceRegistry, TenancySettings?)"/>, plus the
    /// source the caller's own settings name as its table's home (the outbox publish target, the
    /// internal-command scheduling target), unless that setting names Cosmos.
    /// </summary>
    /// <param name="registry">The entity-to-source registry.</param>
    /// <param name="resolver">Resolves the configured logical source to its physical one.</param>
    /// <param name="configuredEngine">The engine the caller's settings name.</param>
    /// <param name="configuredDatabaseName">The logical database name the caller's settings name.</param>
    /// <param name="tenancy">Bound tenancy settings, or null when tenancy was never registered.</param>
    /// <returns>The targets to sweep, in a deterministic order.</returns>
    internal static List<TenantDataSourceTarget> ExpandRelational(
        IEntityDataSourceRegistry registry,
        IDataSourceResolver resolver,
        DataSource configuredEngine,
        string configuredDatabaseName,
        TenancySettings? tenancy)
    {
        var sources = RelationalSourcesInUse(registry);

        if (configuredEngine != DataSource.CosmosDB)
        {
            sources = sources.Append(resolver.ResolveLogical(configuredEngine, configuredDatabaseName));
        }

        return Expand(sources.Distinct(), tenancy);
    }

    /// <summary>
    /// Creates a DI scope for one target and, when the target is a tenant's own database, sets that
    /// tenant on the scope. Call it BEFORE asking the scope for a context: the tenant is what routes
    /// the scoped context factory to the tenant's connection string, and it is also what the query
    /// filter reads.
    /// </summary>
    /// <param name="scopeFactory">The root scope factory.</param>
    /// <param name="target">The target the scope will work against.</param>
    /// <returns>The new scope; the caller owns its disposal.</returns>
    public static IServiceScope CreateTenantScope(this IServiceScopeFactory scopeFactory, TenantDataSourceTarget target)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);

        var scope = scopeFactory.CreateScope();
        if (target.TenantId is { } tenantId)
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        }

        return scope;
    }

    private static IEnumerable<DataSourceKey> RelationalSourcesInUse(IEntityDataSourceRegistry registry) =>
        registry.GetPhysicalSourcesInUse().Where(key => key.Engine != DataSource.CosmosDB);
}

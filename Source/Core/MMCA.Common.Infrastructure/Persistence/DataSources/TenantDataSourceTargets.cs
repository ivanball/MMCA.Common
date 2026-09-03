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
}

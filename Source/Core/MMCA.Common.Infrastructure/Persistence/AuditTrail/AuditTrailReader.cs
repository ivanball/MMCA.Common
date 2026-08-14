using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auditing;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.Infrastructure.Persistence.AuditTrail;

/// <summary>
/// Reads recorded changes from the <c>AuditTrailEntries</c> table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Known v1 limitation: the Default source only.</b> Trail rows are written to whichever database
/// holds the entity that changed (that is what makes the write atomic), so a host that splits its
/// modules across several databases has several trail tables. This reader queries exactly one of
/// them: the <c>Default</c> database of the engine named by <c>AuditTrail:DataSource</c>. For the
/// monolith, where every source collapses onto <c>Default</c>, that is the whole trail. For a
/// database-per-module host it is the trail of the modules living in the default database; reading
/// another module's history needs a per-source overload, which is deliberately deferred rather than
/// guessed at.
/// </para>
/// <para>
/// Returns an empty list rather than throwing when the trail is not enabled: the read surface is
/// registered by <c>AddAuditTrail</c>, which a host may call before flipping
/// <c>AuditTrail:Enabled</c> per environment.
/// </para>
/// </remarks>
/// <param name="dbContextFactory">Scoped factory resolving the context for the trail's data source.</param>
/// <param name="dataSourceResolver">Resolver mapping the configured engine to its physical Default source.</param>
/// <param name="options">Bound audit-trail settings.</param>
internal sealed class AuditTrailReader(
    IDbContextFactory dbContextFactory,
    IDataSourceResolver dataSourceResolver,
    IOptions<AuditTrailSettings> options) : IAuditTrailReader
{
    private readonly AuditTrailSettings _settings = options.Value;

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditTrailEntryDTO>> GetForEntityAsync(
        string entityType,
        string entityKey,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var effectivePage = page < 1 ? 1 : page;
        var effectivePageSize = pageSize < 1 ? 1 : pageSize;

        var context = dbContextFactory.GetDbContext(
            dataSourceResolver.ResolveLogical(_settings.DataSource, DataSourceKey.DefaultName));

        if (context.Model.FindEntityType(typeof(AuditTrailEntry)) is null)
        {
            return [];
        }

        return await context.Set<AuditTrailEntry>().AsNoTracking()
            .Where(row => row.EntityType == entityType && row.EntityKey == entityKey)
            .OrderByDescending(row => row.ChangedOn)
            .ThenByDescending(row => row.Id)
            .Skip((effectivePage - 1) * effectivePageSize)
            .Take(effectivePageSize)
            .Select(row => new AuditTrailEntryDTO
            {
                Id = row.Id,
                EntityType = row.EntityType,
                EntityKey = row.EntityKey,
                PropertyName = row.PropertyName,
                OldValue = row.OldValue,
                NewValue = row.NewValue,
                Operation = row.Operation,
                ChangedBy = row.ChangedBy,
                ChangedOn = row.ChangedOn,
                CorrelationId = row.CorrelationId,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}

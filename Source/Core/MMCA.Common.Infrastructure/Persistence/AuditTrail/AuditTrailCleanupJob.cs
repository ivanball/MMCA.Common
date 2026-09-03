using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Tenancy;

namespace MMCA.Common.Infrastructure.Persistence.AuditTrail;

/// <summary>
/// The framework's own recurring job: purges <see cref="AuditTrailEntry"/> rows older than
/// <see cref="AuditTrailSettings.RetentionDays"/> from every relational data source in use.
/// </summary>
/// <remarks>
/// <para>
/// A change trail grows monotonically and stores values that may describe a data subject, so a
/// retention window is not optional housekeeping: it is what keeps the table from becoming both a
/// cost centre and an unbounded data-protection liability (ADR-005).
/// </para>
/// <para>
/// <b>It only runs if the host also runs the scheduler.</b> <c>AddAuditTrail</c> registers this job,
/// but a job with no runner never executes: the host must also call <c>AddScheduledJobs</c> and set
/// <c>Scheduler:Enabled</c>. A host that records the trail without the scheduler is fully supported
/// and keeps recording; pruning is then the operator's job.
/// </para>
/// <para>
/// Deletion is set-based and batched: ids are read a page at a time and removed with
/// <c>ExecuteDelete</c>, so a first sweep over a long-neglected table does not become one enormous
/// transaction, and no row is ever materialized as an entity.
/// </para>
/// </remarks>
/// <param name="dbContextFactory">Scoped factory for the context of each data source being swept.</param>
/// <param name="entityDataSourceRegistry">Registry enumerating the physical data sources in use.</param>
/// <param name="logger">Logger for sweep diagnostics.</param>
/// <param name="options">Bound audit-trail settings (the retention window).</param>
/// <param name="timeProvider">Clock abstraction for the retention cutoff.</param>
/// <param name="scopeFactory">
/// Creates the per-tenant scope a database-per-tenant sweep needs. Defaulted: a host without
/// tenancy never leaves the job's own scope.
/// </param>
/// <param name="tenancyOptions">
/// Bound tenancy settings, used only to discover tenants whose own database holds its own trail
/// table (which the shared sweep never reaches).
/// </param>
internal sealed partial class AuditTrailCleanupJob(
    IDbContextFactory dbContextFactory,
    IEntityDataSourceRegistry entityDataSourceRegistry,
    ILogger<AuditTrailCleanupJob> logger,
    IOptions<AuditTrailSettings> options,
    TimeProvider timeProvider,
    IServiceScopeFactory? scopeFactory = null,
    IOptions<TenancySettings>? tenancyOptions = null) : IScheduledJob
{
    /// <summary>The number of rows removed per statement.</summary>
    internal const int BatchSize = 1000;

    private readonly AuditTrailSettings _settings = options.Value;

    /// <inheritdoc />
    public string Name => "audit-trail-cleanup";

    /// <inheritdoc />
    /// <remarks>Daily at 03:00 UTC: off the daily peak for every time zone this framework runs in.</remarks>
    public string CronExpression => "0 3 * * *";

    /// <inheritdoc />
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!_settings.Enabled)
        {
            return;
        }

        var cutoff = timeProvider.GetUtcNow().UtcDateTime.Subtract(TimeSpan.FromDays(_settings.RetentionDays));

        var relationalSources = entityDataSourceRegistry.GetPhysicalSourcesInUse()
            .Where(key => key.Engine != DataSource.CosmosDB)
            .Distinct();

        foreach (var target in TenantDataSourceTargets.Expand(relationalSources, tenancyOptions?.Value))
        {
            await PurgeTargetAsync(target, cutoff, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sweeps one target. The shared database is swept on the job's own scope; a tenant that keeps
    /// its own database needs a fresh scope with that tenant set, because the scoped context factory
    /// binds one database per scope and the job's scope is already bound to the shared one.
    /// </summary>
    private async Task PurgeTargetAsync(
        TenantDataSourceTarget target,
        DateTime cutoff,
        CancellationToken cancellationToken)
    {
        if (target.TenantId is null || scopeFactory is null)
        {
            await PurgeWithFactoryAsync(dbContextFactory, target, cutoff, cancellationToken).ConfigureAwait(false);
            return;
        }

        using var scope = scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(target.TenantId);
        await PurgeWithFactoryAsync(
            scope.ServiceProvider.GetRequiredService<IDbContextFactory>(),
            target,
            cutoff,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves the target's context from <paramref name="factory"/> and purges it.</summary>
    private async Task PurgeWithFactoryAsync(
        IDbContextFactory factory,
        TenantDataSourceTarget target,
        DateTime cutoff,
        CancellationToken cancellationToken)
    {
        var context = factory.GetDbContext(target.Source);

        // Cosmos is already excluded, but a relational source can still lack the table when the
        // host disabled the trail after it was written, so the model is the authority.
        if (context.Model.FindEntityType(typeof(AuditTrailEntry)) is null)
        {
            return;
        }

        var deleted = await PurgeSourceAsync(context, cutoff, cancellationToken).ConfigureAwait(false);
        if (deleted > 0)
        {
            var sourceName = target.ToString();
            LogPurged(logger, deleted, sourceName);
        }
    }

    /// <summary>
    /// Removes every trail row older than <paramref name="cutoff"/> from one data source, a batch at
    /// a time.
    /// </summary>
    private static async Task<int> PurgeSourceAsync(
        DbContext context,
        DateTime cutoff,
        CancellationToken cancellationToken)
    {
        var total = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            // The ids are read first and deleted by id, rather than deleting straight from a limited
            // query: every relational provider translates an IN list, while a limited DELETE is not
            // universally supported.
            var ids = await context.Set<AuditTrailEntry>().AsNoTracking()
                .Where(row => row.ChangedOn < cutoff)
                .OrderBy(row => row.ChangedOn)
                .Select(row => row.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (ids.Count == 0)
            {
                break;
            }

            total += await context.Set<AuditTrailEntry>()
                .Where(row => ids.Contains(row.Id))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            if (ids.Count < BatchSize)
            {
                break;
            }
        }

        return total;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged {Count} audit trail rows older than retention from {DataSourceName}")]
    private static partial void LogPurged(ILogger logger, int count, string dataSourceName);
}

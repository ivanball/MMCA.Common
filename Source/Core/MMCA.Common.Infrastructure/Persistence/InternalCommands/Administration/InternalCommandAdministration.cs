using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.InternalCommands;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;
using MMCA.Common.Infrastructure.Persistence.Tenancy;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Infrastructure.Persistence.InternalCommands.Administration;

/// <summary>
/// EF-backed <see cref="IInternalCommandAdministration"/> over the same targets the
/// <c>InternalCommandProcessor</c> drains and the <see cref="InternalCommandCleanupService"/> sweeps:
/// every relational physical source in use, plus the configured scheduling target, expanded per
/// tenant that keeps its own copy of a source.
/// <para>
/// Each target is visited in its OWN DI scope, exactly as the two background services do, because a
/// tenant target only routes to the right database once <c>ITenantContext</c> has been set for that
/// scope. Requeue and purge are expressed as one set-based statement per target rather than as loaded
/// entities: an operator clearing a backlog is touching thousands of rows, and none of the values
/// written depend on the row's current state.
/// </para>
/// </summary>
/// <param name="scopeFactory">Factory for creating a DI scope per visited target.</param>
/// <param name="logger">Logger for requeue and purge diagnostics.</param>
/// <param name="options">Queue settings supplying <c>MaxAttempts</c> and the scheduling target.</param>
/// <param name="entityDataSourceRegistry">Registry enumerating the physical data sources in use.</param>
/// <param name="dataSourceResolver">Resolver for the configured scheduling target.</param>
/// <param name="signal">Signal that wakes the processor as soon as a requeue lands.</param>
/// <param name="timeProvider">Clock behind the purge threshold; defaults to
/// <see cref="TimeProvider.System"/> so tests can drive it deterministically.</param>
/// <param name="tenancyOptions">Bound tenancy settings, used to expand per-tenant copies of a source.</param>
public sealed partial class InternalCommandAdministration(
    IServiceScopeFactory scopeFactory,
    ILogger<InternalCommandAdministration> logger,
    IOptions<InternalCommandsSettings> options,
    IEntityDataSourceRegistry entityDataSourceRegistry,
    IDataSourceResolver dataSourceResolver,
    IInternalCommandSignal signal,
    TimeProvider? timeProvider = null,
    IOptions<TenancySettings>? tenancyOptions = null) : IInternalCommandAdministration
{
    /// <summary>Upper bound on one page, so an admin call cannot ask for the whole table at once.</summary>
    private const int MaxPageSize = 500;

    private static readonly Error SkipError =
        Error.Validation("InternalCommands.InvalidSkip", "Skip must be zero or greater.");

    private static readonly Error TakeError =
        Error.Validation("InternalCommands.InvalidTake", $"Take must be between 1 and {MaxPageSize}.");

    private static readonly Error OlderThanError =
        Error.Validation("InternalCommands.InvalidOlderThan", "OlderThan must be zero or greater.");

    private readonly InternalCommandsSettings _settings = options.Value;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<Result<long>> CountPendingAsync(string? dataSource, CancellationToken cancellationToken)
    {
        var targets = SelectTargets(dataSource);
        if (targets.Count == 0)
            return Result.Failure<long>(UnknownSourceError(dataSource));

        var maxAttempts = _settings.MaxAttempts;
        var pending = 0L;

        foreach (var target in targets)
        {
            pending += await VisitAsync(
                target,
                async context => await context.Set<InternalCommandMessage>()
                    .Where(c => c.ProcessedOn == null && c.DeadLetteredOn == null && c.Attempts < maxAttempts)
                    .LongCountAsync(cancellationToken)
                    .ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }

        return Result.Success(pending);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<InternalCommandDeadLetter>>> ListDeadLettersAsync(
        string? dataSource,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        if (skip < 0)
            return Result.Failure<IReadOnlyList<InternalCommandDeadLetter>>(SkipError);

        if (take is <= 0 or > MaxPageSize)
            return Result.Failure<IReadOnlyList<InternalCommandDeadLetter>>(TakeError);

        var targets = SelectTargets(dataSource);
        if (targets.Count == 0)
            return Result.Failure<IReadOnlyList<InternalCommandDeadLetter>>(UnknownSourceError(dataSource));

        List<InternalCommandDeadLetter> collected = [];

        foreach (var target in targets)
        {
            // Materialize the name outside the query: it is a constant for this target, and inside
            // the projection it would be a method call EF has to translate.
            var sourceName = target.ToString();

            // Paging is applied across the merged result, not per target: "skip 50" must mean the
            // same thing whether this host owns one database or four. Each target therefore returns
            // at most skip + take rows, which is all the merge can need from it.
            var page = await VisitAsync(
                target,
                async context => await context.Set<InternalCommandMessage>().AsNoTracking()
                    .Where(c => c.DeadLetteredOn != null)
                    .OrderBy(c => c.ScheduledOn)
                    .ThenBy(c => c.Id)
                    .Take(skip + take)
                    .Select(c => new InternalCommandDeadLetter(
                        c.Id,
                        sourceName,
                        c.CommandType,
                        c.ScheduledOn,
                        c.CreatedOn,
                        c.Attempts,
                        c.LastError,
                        c.DeadLetteredOn))
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);

            collected.AddRange(page);
        }

        IReadOnlyList<InternalCommandDeadLetter> result =
        [
            .. collected.OrderBy(d => d.ScheduledOn).ThenBy(d => d.Id).Skip(skip).Take(take),
        ];

        return Result.Success(result);
    }

    /// <inheritdoc />
    public async Task<Result<int>> RequeueAsync(
        string? dataSource,
        IReadOnlyCollection<Guid>? ids,
        CancellationToken cancellationToken)
    {
        var targets = SelectTargets(dataSource);
        if (targets.Count == 0)
            return Result.Failure<int>(UnknownSourceError(dataSource));

        Guid[] idFilter = ids is { Count: > 0 } ? [.. ids] : [];
        var requeued = 0;

        foreach (var target in targets)
        {
            var updated = await VisitAsync(
                target,
                async context =>
                {
                    var query = context.Set<InternalCommandMessage>().Where(c => c.DeadLetteredOn != null);

                    if (idFilter.Length > 0)
                    {
                        query = query.Where(c => idFilter.Contains(c.Id));
                    }

                    // Attempts back to zero and the dead-letter stamp cleared is what returns the row
                    // to the poll's predicate; the lease is cleared so it is claimable on the very
                    // next cycle instead of after LeaseSeconds. LastError survives on purpose: it is
                    // the record of WHY this row needed requeuing, and a requeue that erased it would
                    // destroy the only evidence.
                    return await query.ExecuteUpdateAsync(
                        s => s
                            .SetProperty(c => c.Attempts, 0)
                            .SetProperty(c => c.DeadLetteredOn, (DateTime?)null)
                            .SetProperty(c => c.ClaimedUntil, (DateTime?)null)
                            .SetProperty(c => c.ClaimedBy, (Guid?)null),
                        cancellationToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);

            if (updated > 0)
            {
                LogRequeued(logger, updated, target.ToString());
            }

            requeued += updated;
        }

        if (requeued > 0)
        {
            // Wake the processor rather than leaving the requeue to the next polling interval.
            signal.Signal();
        }

        return Result.Success(requeued);
    }

    /// <inheritdoc />
    public async Task<Result<int>> PurgeProcessedAsync(
        string? dataSource,
        TimeSpan olderThan,
        CancellationToken cancellationToken)
    {
        if (olderThan < TimeSpan.Zero)
            return Result.Failure<int>(OlderThanError);

        var targets = SelectTargets(dataSource);
        if (targets.Count == 0)
            return Result.Failure<int>(UnknownSourceError(dataSource));

        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - olderThan;
        var purged = 0;

        foreach (var target in targets)
        {
            var deleted = await VisitAsync(
                target,
                async context => await context.Set<InternalCommandMessage>()
                    .Where(c => c.ProcessedOn != null && c.ProcessedOn <= cutoff)
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);

            if (deleted > 0)
            {
                var sourceName = target.ToString();
                LogPurged(logger, deleted, sourceName);
            }

            purged += deleted;
        }

        return Result.Success(purged);
    }

    private static Error UnknownSourceError(string? dataSource) =>
        Error.NotFoundError(
            "InternalCommands.UnknownDataSource",
            $"No internal-command data source named '{dataSource}' is owned by this host.");

    /// <summary>
    /// The queue units this host owns, optionally narrowed to one by name. Recomputed per call for
    /// the same reason the processor recomputes it per cycle: module assemblies can register entities
    /// after startup.
    /// </summary>
    private List<TenantDataSourceTarget> SelectTargets(string? dataSource)
    {
        IEnumerable<DataSourceKey> sources = entityDataSourceRegistry.GetPhysicalSourcesInUse()
            .Where(k => k.Engine != DataSource.CosmosDB);

        if (_settings.DataSource != DataSource.CosmosDB)
        {
            sources = sources.Append(dataSourceResolver.ResolveLogical(_settings.DataSource, _settings.DatabaseName));
        }

        var targets = TenantDataSourceTargets.Expand([.. sources.Distinct()], tenancyOptions?.Value);

        return dataSource is null
            ? targets
            : [.. targets.Where(t => string.Equals(t.ToString(), dataSource, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>
    /// Runs <paramref name="work"/> against one target in its own scope, setting the tenant BEFORE
    /// the context is asked for (the tenant is what routes the scoped factory to that tenant's
    /// database, and it is also what the query filter reads).
    /// </summary>
    private async Task<T> VisitAsync<T>(
        TenantDataSourceTarget target,
        Func<ApplicationDbContext, Task<T>> work,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = scopeFactory.CreateScope();

        if (target.TenantId is { } tenantId)
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        }

        var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory>();
        return await work(dbContextFactory.GetDbContext(target.Source)).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Requeued {Count} dead-lettered internal commands in {DataSourceName}: attempt counts reset and claims cleared")]
    private static partial void LogRequeued(ILogger logger, int count, string dataSourceName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged {Count} completed internal commands from {DataSourceName} on operator request")]
    private static partial void LogPurged(ILogger logger, int count, string dataSourceName);
}

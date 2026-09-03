using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Tenancy;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Infrastructure.Persistence.Outbox;

/// <summary>
/// EF-backed <see cref="IOutboxAdministration"/> over the same outbox targets the
/// <see cref="OutboxProcessor"/> drains and the <see cref="OutboxCleanupService"/> sweeps: every
/// relational physical source in use, plus the configured publish target, expanded per tenant that
/// keeps its own copy of a source.
/// <para>
/// Each target is visited in its OWN DI scope, exactly as the two background services do, because a
/// tenant target only routes to the right database once <c>ITenantContext</c> has been set for that
/// scope. Replay is expressed as one set-based <c>UPDATE</c> per target rather than as loaded
/// entities: an operator replaying a backlog is replaying thousands of rows, and none of the values
/// written depend on the row's current state.
/// </para>
/// </summary>
/// <param name="scopeFactory">Factory for creating a DI scope per visited target.</param>
/// <param name="logger">Logger for replay diagnostics.</param>
/// <param name="outboxOptions">Outbox settings supplying <c>MaxRetries</c> and the publish target.</param>
/// <param name="entityDataSourceRegistry">Registry enumerating the physical data sources in use.</param>
/// <param name="dataSourceResolver">Resolver for the configured outbox publish target.</param>
/// <param name="outboxSignal">Signal that wakes the processor as soon as a replay lands.</param>
/// <param name="tenancyOptions">Bound tenancy settings, used to expand per-tenant copies of a source.</param>
public sealed partial class OutboxAdministration(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxAdministration> logger,
    IOptions<OutboxSettings> outboxOptions,
    IEntityDataSourceRegistry entityDataSourceRegistry,
    IDataSourceResolver dataSourceResolver,
    IOutboxSignal outboxSignal,
    IOptions<TenancySettings>? tenancyOptions = null) : IOutboxAdministration
{
    /// <summary>Upper bound on one page, so an admin call cannot ask for the whole table at once.</summary>
    private const int MaxPageSize = 500;

    private static readonly Error SkipError =
        Error.Validation("Outbox.InvalidSkip", "Skip must be zero or greater.");

    private static readonly Error TakeError =
        Error.Validation("Outbox.InvalidTake", $"Take must be between 1 and {MaxPageSize}.");

    private readonly OutboxSettings _settings = outboxOptions.Value;

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<OutboxDeadLetter>>> ListDeadLettersAsync(
        string? dataSource,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        if (skip < 0)
            return Result.Failure<IReadOnlyList<OutboxDeadLetter>>([SkipError]);

        if (take is <= 0 or > MaxPageSize)
            return Result.Failure<IReadOnlyList<OutboxDeadLetter>>([TakeError]);

        var targets = SelectTargets(dataSource);
        if (targets.Count == 0)
            return Result.Failure<IReadOnlyList<OutboxDeadLetter>>([UnknownSourceError(dataSource)]);

        var maxRetries = _settings.MaxRetries;
        List<OutboxDeadLetter> collected = [];

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
                async context => await context.Set<OutboxMessage>().AsNoTracking()
                    .Where(m => m.ProcessedOn == null && m.RetryCount >= maxRetries)
                    .OrderBy(m => m.OccurredOn)
                    .ThenBy(m => m.Id)
                    .Take(skip + take)
                    .Select(m => new OutboxDeadLetter(
                        m.Id,
                        sourceName,
                        m.EventType,
                        m.OccurredOn,
                        m.RetryCount,
                        m.LastError,
                        m.OrderingKey))
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);

            collected.AddRange(page);
        }

        IReadOnlyList<OutboxDeadLetter> result =
        [
            .. collected.OrderBy(d => d.OccurredOn).ThenBy(d => d.Id).Skip(skip).Take(take),
        ];

        return Result.Success(result);
    }

    /// <inheritdoc />
    public async Task<Result<int>> ReplayDeadLettersAsync(
        string? dataSource,
        IReadOnlyCollection<Guid>? ids,
        CancellationToken cancellationToken)
    {
        var targets = SelectTargets(dataSource);
        if (targets.Count == 0)
            return Result.Failure<int>([UnknownSourceError(dataSource)]);

        var maxRetries = _settings.MaxRetries;
        Guid[] idFilter = ids is { Count: > 0 } ? [.. ids] : [];
        var replayed = 0;

        foreach (var target in targets)
        {
            var updated = await VisitAsync(
                target,
                async context =>
                {
                    var query = context.Set<OutboxMessage>()
                        .Where(m => m.ProcessedOn == null && m.RetryCount >= maxRetries);

                    if (idFilter.Length > 0)
                    {
                        query = query.Where(m => idFilter.Contains(m.Id));
                    }

                    // RetryCount back to zero is what returns the row to the poll's predicate; the
                    // lease is cleared so it is claimable on the very next cycle instead of after
                    // LeaseSeconds. LastError survives on purpose: it is the record of WHY this row
                    // needed replaying, and a replay that erased it would destroy the only evidence.
                    return await query.ExecuteUpdateAsync(
                        s => s
                            .SetProperty(m => m.RetryCount, 0)
                            .SetProperty(m => m.LockedUntil, (DateTime?)null)
                            .SetProperty(m => m.LockToken, (Guid?)null),
                        cancellationToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);

            if (updated > 0)
            {
                LogReplayed(logger, updated, target.ToString());
            }

            replayed += updated;
        }

        if (replayed > 0)
        {
            // Wake the processor rather than leaving the replay to the next polling interval, which
            // deployed environments set as high as 300s.
            outboxSignal.Signal();
        }

        return Result.Success(replayed);
    }

    /// <inheritdoc />
    public async Task<Result<long>> CountPendingAsync(string? dataSource, CancellationToken cancellationToken)
    {
        var targets = SelectTargets(dataSource);
        if (targets.Count == 0)
            return Result.Failure<long>([UnknownSourceError(dataSource)]);

        var maxRetries = _settings.MaxRetries;
        var pending = 0L;

        foreach (var target in targets)
        {
            pending += await VisitAsync(
                target,
                async context => await context.Set<OutboxMessage>()
                    .Where(m => m.ProcessedOn == null && m.RetryCount < maxRetries)
                    .LongCountAsync(cancellationToken)
                    .ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }

        return Result.Success(pending);
    }

    private static Error UnknownSourceError(string? dataSource) =>
        Error.NotFoundError(
            "Outbox.UnknownDataSource",
            $"No outbox data source named '{dataSource}' is owned by this host.");

    /// <summary>
    /// The outbox units this host owns, optionally narrowed to one by name. Recomputed per call for
    /// the same reason the processor recomputes it per cycle: module assemblies can register
    /// entities after startup.
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Replayed {Count} dead-lettered outbox messages in {DataSourceName}: retry counts reset and claims cleared")]
    private static partial void LogReplayed(ILogger logger, int count, string dataSourceName);
}

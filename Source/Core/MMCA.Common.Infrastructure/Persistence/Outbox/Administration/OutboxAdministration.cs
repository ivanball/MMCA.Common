using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Infrastructure.Persistence.Outbox.Administration;

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
/// <param name="tableTargets">Decides which databases (and per-tenant copies) hold an outbox table this host owns.</param>
/// <param name="outboxSignal">Signal that wakes the processor as soon as a replay lands.</param>
/// <param name="messageBusOptions">Message-bus settings; <see cref="RepublishProcessedAsync"/> refuses
/// to run unless the consumer inbox is enabled.</param>
public sealed partial class OutboxAdministration(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxAdministration> logger,
    IOptions<OutboxSettings> outboxOptions,
    FrameworkTableTargets tableTargets,
    IOutboxSignal outboxSignal,
    IOptions<MessageBusSettings> messageBusOptions) : IOutboxAdministration
{
    /// <summary>Upper bound on one page, so an admin call cannot ask for the whole table at once.</summary>
    private const int MaxPageSize = 500;

    private static readonly Error SkipError =
        Error.Validation("Outbox.InvalidSkip", "Skip must be zero or greater.");

    private static readonly Error TakeError =
        Error.Validation("Outbox.InvalidTake", $"Take must be between 1 and {MaxPageSize}.");

    /// <summary>The widest window <see cref="RepublishProcessedAsync"/> accepts.</summary>
    private static readonly TimeSpan MaxRepublishWindow = TimeSpan.FromDays(7);

    private static readonly Error RepublishWindowNotUtcError =
        Error.Validation("Outbox.RepublishWindowNotUtc", "The republish window bounds must be UTC (DateTimeKind.Utc).");

    private static readonly Error RepublishWindowEmptyError =
        Error.Validation("Outbox.InvalidRepublishWindow", "The republish window must end after it starts.");

    private static readonly Error RepublishWindowTooWideError =
        Error.Validation("Outbox.RepublishWindowTooWide", "The republish window must not exceed 7 days.");

    private static readonly Error RepublishRequiresInboxError =
        Error.Conflict(
            "Outbox.RepublishRequiresInbox",
            "Republishing processed integration events needs the consumer inbox (MessageBus:EnableInbox, on by default for a broker transport). Without it every republished event runs its handlers again.");

    private readonly OutboxSettings _settings = outboxOptions.Value;
    private readonly bool _inboxEnabled = messageBusOptions.Value.IsInboxEnabled;

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

        // Widened then clamped: skip + take in int arithmetic wraps negative for a skip near
        // int.MaxValue, and the database then rejects the negative FETCH/LIMIT. Clamped, a deep page
        // simply comes back empty.
        var perTarget = (int)Math.Min((long)skip + take, int.MaxValue);

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
                    .Take(perTarget)
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
    /// <remarks>
    /// The table stores no event kind: a row's <c>EventType</c> is a stored type identity, and
    /// whether it is an integration event is a property of the type it resolves to. Each source is
    /// therefore asked first for the DISTINCT stored names processed in the window (a handful of
    /// values, read from <c>IX_OutboxMessages_Processed</c>), each name is resolved exactly as the
    /// processor would resolve it, and the update is then one set-based statement over the names
    /// that resolve to an <see cref="IIntegrationEvent"/>. A name this host cannot resolve is left
    /// alone, because the processor could not publish it either.
    /// </remarks>
    public async Task<Result<int>> RepublishProcessedAsync(
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken)
    {
        // The inbox is what turns a republish into a harmless duplicate. Without it (the in-process
        // bus default, or a broker host that set MessageBus:EnableInbox=false) every republished
        // event would run its handlers again, so the call is refused before anything is touched.
        if (!_inboxEnabled)
            return Result.Failure<int>([RepublishRequiresInboxError]);

        var windowError = ValidateRepublishWindow(fromUtc, toUtc);
        if (windowError is not null)
            return Result.Failure<int>([windowError]);

        var targets = SelectTargets(dataSource: null);
        if (targets.Count == 0)
            return Result.Failure<int>([UnknownSourceError(null)]);

        var republished = 0;

        foreach (var target in targets)
        {
            var updated = await VisitAsync(
                target,
                async context =>
                {
                    var processedInWindow = context.Set<OutboxMessage>()
                        .Where(m => m.ProcessedOn != null && m.ProcessedOn >= fromUtc && m.ProcessedOn < toUtc);

                    var storedNames = await processedInWindow
                        .Select(m => m.EventType)
                        .Distinct()
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);

                    string[] integrationNames = [.. storedNames.Where(IsIntegrationEvent)];
                    if (integrationNames.Length == 0)
                    {
                        return 0;
                    }

                    // ProcessedOn back to null is what returns the row to the poll's predicate,
                    // RetryCount back to zero gives it the full retry budget again, and the lease is
                    // cleared so it is claimable on the very next cycle. LastError survives, as on a
                    // dead-letter replay, and OccurredOn is untouched so ordering keys hold.
                    return await processedInWindow
                        .Where(m => integrationNames.Contains(m.EventType))
                        .ExecuteUpdateAsync(
                            s => s
                                .SetProperty(m => m.ProcessedOn, (DateTime?)null)
                                .SetProperty(m => m.RetryCount, 0)
                                .SetProperty(m => m.LockedUntil, (DateTime?)null)
                                .SetProperty(m => m.LockToken, (Guid?)null),
                            cancellationToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);

            // Logged for every source, a zero included: a republish is an operator action taken
            // during an incident or a cutover, and "this source had nothing in the window" is part
            // of its record.
            LogRepublished(logger, updated, target.ToString(), fromUtc, toUtc);
            republished += updated;
        }

        if (republished > 0)
        {
            outboxSignal.Signal();
        }

        return Result.Success(republished);
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

    /// <summary>
    /// Checks a republish window: both bounds UTC, a non-empty range, and no wider than
    /// <see cref="MaxRepublishWindow"/> (wide enough for a deploy overlap or an incident, narrow
    /// enough that a mistyped date cannot republish a month of events).
    /// </summary>
    /// <returns>The validation error, or <see langword="null"/> for a valid window.</returns>
    private static Error? ValidateRepublishWindow(DateTime fromUtc, DateTime toUtc)
    {
        if (fromUtc.Kind != DateTimeKind.Utc || toUtc.Kind != DateTimeKind.Utc)
            return RepublishWindowNotUtcError;

        if (fromUtc >= toUtc)
            return RepublishWindowEmptyError;

        return toUtc - fromUtc > MaxRepublishWindow ? RepublishWindowTooWideError : null;
    }

    private static bool IsIntegrationEvent(string storedName) =>
        OutboxMessage.ResolveStoredEventType(storedName) is { } type
        && typeof(IIntegrationEvent).IsAssignableFrom(type);

    private static Error UnknownSourceError(string? dataSource) =>
        Error.NotFoundError(
            "Outbox.UnknownDataSource",
            $"No outbox data source named '{dataSource}' is owned by this host.");

    /// <summary>
    /// The outbox units this host owns, optionally narrowed to one by name. Recomputed per call for
    /// the same reason the processor recomputes it per cycle: module assemblies can register
    /// entities after startup.
    /// </summary>
    private IReadOnlyList<TenantDataSourceTarget> SelectTargets(string? dataSource) =>
        tableTargets.Named(_settings.DataSource, _settings.DatabaseName, dataSource);

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

        using var scope = scopeFactory.CreateTenantScope(target);

        var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory>();
        return await work(dbContextFactory.GetDbContext(target.Source)).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Replayed {Count} dead-lettered outbox messages in {DataSourceName}: retry counts reset and claims cleared")]
    private static partial void LogReplayed(ILogger logger, int count, string dataSourceName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Republished {Count} processed integration-event outbox messages in {DataSourceName} for the window {FromUtc:O} to {ToUtc:O}: processed stamps, retry counts and claims cleared")]
    private static partial void LogRepublished(ILogger logger, int count, string dataSourceName, DateTime fromUtc, DateTime toUtc);
}

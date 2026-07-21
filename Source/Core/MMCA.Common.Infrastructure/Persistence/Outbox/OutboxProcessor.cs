using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.Infrastructure.Persistence.Outbox;

/// <summary>
/// Background service that polls the outbox tables for unprocessed domain events and
/// dispatches them via <see cref="IDomainEventDispatcher"/>. Acts as a safety net:
/// events are normally dispatched in-process immediately after persistence, but if
/// that dispatch fails (e.g. process crash), this processor retries them.
/// <para>
/// Every relational physical data source in use by this host has its own
/// <c>OutboxMessages</c> table; each polling cycle drains them all. A host therefore only
/// processes the outboxes of its own databases — services with separate databases never race
/// for each other's messages.
/// </para>
/// </summary>
/// <param name="scopeFactory">Factory for creating DI scopes per processing cycle.</param>
/// <param name="logger">Logger for processing diagnostics.</param>
/// <param name="outboxOptions">Configurable outbox processing settings.</param>
/// <param name="outboxSignal">Signal to wait on between polling cycles for immediate wakeup.</param>
/// <param name="entityDataSourceRegistry">Registry enumerating the physical data sources in use.</param>
/// <param name="dataSourceResolver">Resolver for the configured outbox publish target.</param>
/// <param name="timeProvider">Clock abstraction for the startup delay and lease/eligibility timestamps;
/// defaults to <see cref="TimeProvider.System"/> so tests can drive the loop deterministically.</param>
public sealed partial class OutboxProcessor(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxProcessor> logger,
    IOptions<OutboxSettings> outboxOptions,
    IOutboxSignal outboxSignal,
    IEntityDataSourceRegistry entityDataSourceRegistry,
    IDataSourceResolver dataSourceResolver,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly OutboxSettings _settings = outboxOptions.Value;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Name of the per-cycle poll activity wrapping the outbox fetch query. Must stay in sync
    /// with <c>OutboxPollFilterProcessor</c> in MMCA.Common.Aspire, which suppresses these spans
    /// and their SqlClient children from telemetry export (Aspire has no project references, so
    /// the string is deliberately duplicated there).
    /// </summary>
    internal const string PollActivityName = "OutboxPoll";

    /// <summary>Floor for the computed wait so an overdue pending message cannot hot-loop the processor.</summary>
    private static readonly TimeSpan MinimumWait = TimeSpan.FromSeconds(1);

    private static readonly ActivitySource OutboxActivitySource = new("MMCA.Common.Outbox");
    private static readonly Meter OutboxMeter = new("MMCA.Common.Outbox");
    private static readonly Counter<long> DeadLetterCounter = OutboxMeter.CreateCounter<long>(
        "outbox.dead_letter.count",
        "messages",
        "Number of outbox messages dead-lettered due to unresolvable event types");

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Brief startup delay so the application finishes initializing before we start polling.
        await _timeProvider.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);

        if (GetOutboxSources().Count == 0)
        {
            LogOutboxDisabled(logger);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            OutboxCycleResult cycle = default;
            try
            {
                cycle = await ProcessPendingMessagesAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown — exit gracefully.
                break;
            }
            catch (Exception ex)
            {
                LogProcessingError(logger, ex);
            }

            if (cycle.HasMoreEligibleWork)
            {
                // A full batch was drained with progress — more eligible rows may be waiting.
                continue;
            }

            // Wait for a signal (new outbox entries written), the moment the earliest pending
            // message becomes eligible (smart wait), or the fallback polling interval —
            // whichever comes first.
            var wait = ComputeWaitTime(
                cycle.EarliestPendingOccurredOn,
                _timeProvider.GetUtcNow().UtcDateTime,
                TimeSpan.FromSeconds(_settings.ProcessingDelaySeconds),
                TimeSpan.FromSeconds(_settings.PollingIntervalSeconds));
            await outboxSignal.WaitAsync(wait, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Computes how long to wait before the next polling cycle: until the earliest pending
    /// message becomes eligible (<paramref name="earliestPendingOccurredOn"/> plus the
    /// processing delay), capped at the polling interval and floored at one second to avoid
    /// hot-looping. Failed-but-already-eligible messages never shorten the wait — they retry
    /// on the next signal or interval, which throttles permanently failing messages.
    /// </summary>
    internal static TimeSpan ComputeWaitTime(
        DateTime? earliestPendingOccurredOn,
        DateTime utcNow,
        TimeSpan processingDelay,
        TimeSpan pollingInterval)
    {
        if (earliestPendingOccurredOn is null)
        {
            return pollingInterval;
        }

        var untilEligible = earliestPendingOccurredOn.Value + processingDelay - utcNow;
        if (untilEligible < MinimumWait)
        {
            untilEligible = MinimumWait;
        }

        return untilEligible < pollingInterval ? untilEligible : pollingInterval;
    }

    /// <summary>
    /// The relational physical sources whose outbox tables this host owns: every source backing a
    /// registered entity plus the configured publish target (Cosmos has no outbox table).
    /// Recomputed per cycle — cheap, and tolerant of module assemblies loading after startup.
    /// </summary>
    private List<DataSourceKey> GetOutboxSources()
    {
        IEnumerable<DataSourceKey> sources = entityDataSourceRegistry.GetPhysicalSourcesInUse()
            .Where(k => k.Engine != DataSource.CosmosDB);

        if (_settings.DataSource != DataSource.CosmosDB)
        {
            sources = sources.Append(dataSourceResolver.ResolveLogical(_settings.DataSource, _settings.DatabaseName));
        }

        return [.. sources.Distinct()];
    }

    /// <summary>
    /// Drains every outbox source once and aggregates the per-source results: any source with
    /// more eligible work triggers an immediate re-poll, and the earliest pending timestamp
    /// across all sources drives the smart wait.
    /// </summary>
    internal async Task<OutboxCycleResult> ProcessPendingMessagesAsync(CancellationToken cancellationToken)
    {
        var hasMoreEligibleWork = false;
        DateTime? earliestPendingOccurredOn = null;

        foreach (var source in GetOutboxSources())
        {
            try
            {
                var result = await ProcessSourceAsync(source, cancellationToken).ConfigureAwait(false);
                hasMoreEligibleWork |= result.HasMoreEligibleWork;
                if (result.EarliestPendingOccurredOn is { } pending
                    && (earliestPendingOccurredOn is null || pending < earliestPendingOccurredOn))
                {
                    earliestPendingOccurredOn = pending;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One unreachable database must not starve the other sources' outboxes.
                // A failing source contributes nothing to the wait — its rows are retried
                // on the next signal or polling interval.
                LogSourceProcessingError(logger, source.ToString(), ex);
            }
        }

        return new OutboxCycleResult(hasMoreEligibleWork, earliestPendingOccurredOn);
    }

    private async Task<OutboxCycleResult> ProcessSourceAsync(DataSourceKey source, CancellationToken cancellationToken)
    {
        var sourceName = source.ToString();
        using var scope = scopeFactory.CreateScope();
        var dbContextFactory = scope.ServiceProvider.GetRequiredService<DbContexts.Factory.IDbContextFactory>();
        var context = dbContextFactory.GetDbContext(source);
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher>();
        var messageBus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var cutoff = now.Subtract(TimeSpan.FromSeconds(_settings.ProcessingDelaySeconds));

        var messages = await FetchCandidatesAsync(context, sourceName, now, cancellationToken).ConfigureAwait(false);

        // Split the ordered batch: the eligible prefix is processed now; the pending remainder
        // only informs how long to wait before the next cycle.
        var eligibleCount = 0;
        while (eligibleCount < messages.Count && messages[eligibleCount].OccurredOn < cutoff)
        {
            eligibleCount++;
        }

        DateTime? earliestPending = eligibleCount < messages.Count ? messages[eligibleCount].OccurredOn : null;

        if (eligibleCount == 0)
        {
            return new OutboxCycleResult(HasMoreEligibleWork: false, earliestPending);
        }

        var toProcess = await ClaimEligibleAsync(context, messages, eligibleCount, now, cancellationToken)
            .ConfigureAwait(false);

        if (toProcess.Count == 0)
        {
            // Another replica claimed the whole prefix between fetch and claim.
            return new OutboxCycleResult(HasMoreEligibleWork: false, earliestPending);
        }

        LogProcessingBatch(logger, toProcess.Count, sourceName);

        var processedAny = await DispatchMessagesAsync(
            toProcess, source, dispatcher, messageBus, cancellationToken).ConfigureAwait(false);

        // Use the base DbContext.SaveChangesAsync to bypass audit stamping and event dispatch.
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // A full eligible batch with progress means more eligible rows may be waiting; the
        // progress requirement stops a fully-failing batch from hot-spinning the processor.
        return new OutboxCycleResult(
            HasMoreEligibleWork: eligibleCount == _settings.BatchSize && processedAny,
            earliestPending);
    }

    /// <summary>
    /// Fetches the oldest pending rows for one source. The query runs inside its own activity
    /// (explicit using block) so OutboxPollFilterProcessor in MMCA.Common.Aspire can suppress it
    /// and its SqlClient child span from export — an idle fleet polling around the clock would
    /// otherwise dominate telemetry ingestion. No OccurredOn cutoff in SQL: rows younger than
    /// the processing delay are fetched too, so the caller can smart-wait until the earliest
    /// becomes eligible; ordering by OccurredOn guarantees eligible rows sort before pending
    /// ones. Rows under another replica's unexpired lease are skipped entirely.
    /// </summary>
    private async Task<List<OutboxMessage>> FetchCandidatesAsync(
        ApplicationDbContext context,
        string sourceName,
        DateTime now,
        CancellationToken cancellationToken)
    {
        using var pollActivity = OutboxActivitySource.StartActivity(PollActivityName);
        pollActivity?.SetTag("messaging.outbox.data_source", sourceName);

        return await context.Set<OutboxMessage>()
            .Where(m => m.ProcessedOn == null
                && m.RetryCount < _settings.MaxRetries
                && (m.LockedUntil == null || m.LockedUntil < now))
            .OrderBy(m => m.OccurredOn)
            .Take(_settings.BatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Claims the eligible prefix of the fetched batch with a lease before dispatching: a
    /// concurrent replica's claim update wins or loses per row atomically, so two replicas can
    /// never dispatch the same message (scale-out safety by construction rather than by the
    /// minReplicas:1 deployment convention). A replica that dies mid-batch releases its rows
    /// implicitly when the lease expires. Returns the claimed tracked messages (empty when
    /// another replica claimed the whole prefix between fetch and claim).
    /// </summary>
    private async Task<List<OutboxMessage>> ClaimEligibleAsync(
        ApplicationDbContext context,
        List<OutboxMessage> messages,
        int eligibleCount,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var lockToken = Guid.NewGuid();
        var leaseUntil = now.AddSeconds(_settings.LeaseSeconds);
        var eligibleIds = messages.Take(eligibleCount).Select(m => m.Id).ToArray();

        var claimedCount = await context.Set<OutboxMessage>()
            .Where(m => eligibleIds.Contains(m.Id)
                && m.ProcessedOn == null
                && (m.LockedUntil == null || m.LockedUntil < now))
            .ExecuteUpdateAsync(
                s => s.SetProperty(m => m.LockedUntil, leaseUntil).SetProperty(m => m.LockToken, lockToken),
                cancellationToken)
            .ConfigureAwait(false);

        if (claimedCount == 0)
            return [];

        if (claimedCount == eligibleIds.Length)
            return [.. messages.Take(eligibleCount)];

        // Partial claim: process only the rows carrying this replica's token.
        var claimedIds = await context.Set<OutboxMessage>().AsNoTracking()
            .Where(m => eligibleIds.Contains(m.Id) && m.LockToken == lockToken)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var claimedSet = claimedIds.ToHashSet();
        return [.. messages.Take(eligibleCount).Where(m => claimedSet.Contains(m.Id))];
    }

    /// <summary>
    /// Dispatches each eligible message, marking successes and dead-letters as processed and
    /// incrementing retry counts on failure. Returns whether any message made progress
    /// (dispatched or dead-lettered) this cycle.
    /// </summary>
    private async Task<bool> DispatchMessagesAsync(
        IEnumerable<OutboxMessage> messages,
        DataSourceKey source,
        IDomainEventDispatcher dispatcher,
        IMessageBus messageBus,
        CancellationToken cancellationToken)
    {
        var processedAny = false;
        foreach (var message in messages)
        {
            using var activity = StartOutboxActivity(message, source);
            try
            {
                var domainEvent = message.DeserializeEvent();
                if (domainEvent is null)
                {
                    message.LastError = $"Cannot resolve type: {message.EventType}";
                    message.ProcessedOn = _timeProvider.GetUtcNow().UtcDateTime;
                    processedAny = true;
                    DeadLetterCounter.Add(
                        1,
                        new KeyValuePair<string, object?>("event_type", message.EventType),
                        new KeyValuePair<string, object?>("reason", "type_unresolvable"));
                    LogDeadLetter(logger, message.Id, message.EventType);
                    continue;
                }

                // Integration events route through IMessageBus so the registered transport
                // (in-process for the monolith, MassTransit broker for extracted services)
                // determines delivery. Pure domain events keep the legacy in-process dispatch.
                if (domainEvent is IIntegrationEvent integrationEvent)
                {
                    await messageBus.PublishAsync(integrationEvent, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await dispatcher.DispatchAsync([domainEvent], cancellationToken).ConfigureAwait(false);
                }

                message.ProcessedOn = _timeProvider.GetUtcNow().UtcDateTime;
                processedAny = true;
                LogMessageProcessed(logger, message.Id, message.EventType);
            }
            catch (Exception ex)
            {
                message.RetryCount++;
                message.LastError = ex.Message;
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

                if (message.RetryCount >= _settings.MaxRetries)
                {
                    // The moment of exhaustion is the operator's last loud signal: from here the
                    // row leaves the poll (RetryCount filter) and is eventually purged by
                    // OutboxCleanupService after the dead-letter retention window.
                    DeadLetterCounter.Add(
                        1,
                        new KeyValuePair<string, object?>("event_type", message.EventType),
                        new KeyValuePair<string, object?>("reason", "retries_exhausted"));
                    LogRetriesExhausted(logger, message.Id, message.EventType, message.RetryCount, ex);
                }
                else
                {
                    LogMessageRetry(logger, message.Id, message.RetryCount, ex);
                }
            }
        }

        return processedAny;
    }

    /// <summary>
    /// Starts a new <see cref="Activity"/> linked to the original request's trace context
    /// stored in the outbox message. Returns <see langword="null"/> when no trace context
    /// was captured (e.g., messages written before this feature was added).
    /// </summary>
    private static Activity? StartOutboxActivity(OutboxMessage message, DataSourceKey source)
    {
        if (string.IsNullOrEmpty(message.TraceId) || string.IsNullOrEmpty(message.SpanId))
        {
            return null;
        }

        var parentContext = new ActivityContext(
            ActivityTraceId.CreateFromString(message.TraceId),
            ActivitySpanId.CreateFromString(message.SpanId),
            ActivityTraceFlags.Recorded);

        var activity = OutboxActivitySource.StartActivity(
            "OutboxProcess",
            ActivityKind.Consumer,
            parentContext);

        activity?.SetTag("messaging.outbox.message_id", message.Id.ToString());
        activity?.SetTag("messaging.outbox.event_type", message.EventType);
        activity?.SetTag("messaging.outbox.data_source", source.ToString());

        return activity;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox processor disabled: no relational data sources in use (Cosmos DB does not support the outbox table)")]
    private static partial void LogOutboxDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox processor encountered an error")]
    private static partial void LogProcessingError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox processing failed for data source {DataSourceName}")]
    private static partial void LogSourceProcessingError(ILogger logger, string dataSourceName, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Processing {Count} pending outbox messages from {DataSourceName}")]
    private static partial void LogProcessingBatch(ILogger logger, int count, string dataSourceName);

    // Debug, not Information: this fires once per dispatched message and would otherwise be the
    // single noisiest log line in steady state — a real telemetry-ingestion cost (rubric §31, the published COST guide).
    // Failures stay loud (dead-letter = Error, retry = Warning); success detail is Debug.
    [LoggerMessage(Level = LogLevel.Debug, Message = "Outbox message {MessageId} ({EventType}) dispatched successfully")]
    private static partial void LogMessageProcessed(ILogger logger, Guid messageId, string eventType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {MessageId} dead-lettered: type not resolvable — {EventType}")]
    private static partial void LogDeadLetter(ILogger logger, Guid messageId, string eventType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} failed (attempt {RetryCount})")]
    private static partial void LogMessageRetry(ILogger logger, Guid messageId, int retryCount, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {MessageId} ({EventType}) dead-lettered: retries exhausted after {RetryCount} attempts — the event was never delivered")]
    private static partial void LogRetriesExhausted(ILogger logger, Guid messageId, string eventType, int retryCount, Exception exception);
}

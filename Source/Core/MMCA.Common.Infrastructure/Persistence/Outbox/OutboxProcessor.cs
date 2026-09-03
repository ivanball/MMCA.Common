using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Tenancy;
using MMCA.Common.Shared.Resilience;
using Polly;
using Polly.CircuitBreaker;

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
/// <para>
/// Delivery is at-least-once. A message dispatched but not yet stamped processed (a crash, or a
/// cancellation landing mid-batch) is redelivered only once its claim lease expires, after
/// <c>Outbox:LeaseSeconds</c> (300s by default) rather than immediately on restart, because the
/// claim is persisted before dispatch and the poll skips leased rows. On a graceful shutdown the
/// cancelled batch flushes the stamps it already collected on the way out, which closes that
/// duplicate window for the messages it did deliver.
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
/// <param name="tenancyOptions">
/// Bound tenancy settings, used only to discover tenants that keep their own copy of a source: each
/// such database has its own outbox table that nothing else would drain. Defaulted, so a host
/// without tenancy keeps the previous constructor shape and behavior.
/// </param>
public sealed partial class OutboxProcessor(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxProcessor> logger,
    IOptions<OutboxSettings> outboxOptions,
    IOutboxSignal outboxSignal,
    IEntityDataSourceRegistry entityDataSourceRegistry,
    IDataSourceResolver dataSourceResolver,
    TimeProvider? timeProvider = null,
    IOptions<TenancySettings>? tenancyOptions = null) : BackgroundService
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

    /// <summary>
    /// Budget for the best-effort save that flushes ProcessedOn stamps when a batch is cancelled
    /// mid-flight. Deliberately short: the work is one small UPDATE against an already-open
    /// connection, and anything slower is a dependency that must not delay host shutdown.
    /// </summary>
    private static readonly TimeSpan ShutdownSaveTimeout = TimeSpan.FromSeconds(5);

    private static readonly ActivitySource OutboxActivitySource = new("MMCA.Common.Outbox");

    /// <summary>
    /// Circuit breaker guarding the broker-publish call only (never the database calls: a breaker
    /// on those would open exactly when the processor most needs to persist retry state). Tuned by
    /// <see cref="BrokerResilienceDefaults"/> and carrying NO retry strategy, because the outbox
    /// already owns retry via <c>RetryCount</c> and <see cref="ComputeRetryBackoffSeconds"/>.
    /// <para>
    /// Per instance rather than per process. A host runs one processor, so the practical scope is
    /// the same, while an instance field keeps the breaker state from leaking across the many
    /// processors a test assembly constructs in parallel: one test deliberately failing publishes
    /// would otherwise open a shared circuit under another test's feet.
    /// </para>
    /// </summary>
    private readonly ResiliencePipeline _brokerPublishPipeline = BuildBrokerPublishPipeline();

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Brief startup delay so the application finishes initializing before we start polling.
        await Task.Delay(TimeSpan.FromSeconds(5), _timeProvider, stoppingToken).ConfigureAwait(false);

        if (GetOutboxTargets().Count == 0)
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
    /// The units this cycle visits: every owned source against the shared database, plus one extra
    /// unit per tenant that keeps its own copy of a source. A tenant database has its own
    /// <c>OutboxMessages</c> table, and nothing else opens that database, so without this its events
    /// would sit undelivered forever.
    /// </summary>
    internal List<TenantDataSourceTarget> GetOutboxTargets() =>
        TenantDataSourceTargets.Expand(GetOutboxSources(), tenancyOptions?.Value);

    /// <summary>
    /// Drains every outbox source once and aggregates the per-source results: any source with
    /// more eligible work triggers an immediate re-poll, the earliest pending timestamp
    /// across all sources drives the smart wait, and the backlog observed across all sources is
    /// published to the <c>outbox.pending.depth</c> gauge.
    /// </summary>
    internal async Task<OutboxCycleResult> ProcessPendingMessagesAsync(CancellationToken cancellationToken)
    {
        var hasMoreEligibleWork = false;
        DateTime? earliestPendingOccurredOn = null;
        var pendingDepth = 0L;

        foreach (var target in GetOutboxTargets())
        {
            try
            {
                (OutboxCycleResult result, long sourcePendingDepth) =
                    await ProcessSourceAsync(target, cancellationToken).ConfigureAwait(false);
                pendingDepth += sourcePendingDepth;
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
                LogSourceProcessingError(logger, target.ToString(), ex);
            }
        }

        // Publish what THIS instance observed this cycle. A source that threw contributes zero, so
        // an outage reads as a drop rather than as a stale plateau (see the gauge's remarks).
        OutboxMetrics.SetPendingDepth(pendingDepth);

        return new OutboxCycleResult(hasMoreEligibleWork, earliestPendingOccurredOn);
    }

    /// <summary>
    /// Drains one source and reports both its cycle result and the backlog it observed, so the
    /// caller can sum the depth across sources for the <c>outbox.pending.depth</c> gauge.
    /// </summary>
    private async Task<(OutboxCycleResult Cycle, long PendingDepth)> ProcessSourceAsync(
        TenantDataSourceTarget target,
        CancellationToken cancellationToken)
    {
        var source = target.Source;
        var sourceName = target.ToString();
        using var scope = scopeFactory.CreateScope();

        // Before the context is asked for, not after: the tenant is what routes the scoped factory
        // to this tenant's database, and it is also what the query filter reads.
        if (target.TenantId is { } tenantId)
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        }

        var dbContextFactory = scope.ServiceProvider.GetRequiredService<DbContexts.Factory.IDbContextFactory>();
        var context = dbContextFactory.GetDbContext(source);
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher>();
        var messageBus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var cutoff = now.Subtract(TimeSpan.FromSeconds(_settings.ProcessingDelaySeconds));

        var messages = await FetchCandidatesAsync(context, sourceName, now, cancellationToken).ConfigureAwait(false);
        var pendingDepth = await CountPendingAsync(context, sourceName, messages.Count, now, cancellationToken)
            .ConfigureAwait(false);

        // The fetch is ordered by OccurredOn over exactly the pending predicate, so its first row IS
        // the oldest pending row: the gauge costs no extra query, only a subtraction.
        OutboxMetrics.SetOldestPendingAge(
            sourceName,
            messages.Count == 0 ? 0 : Math.Max((now - messages[0].OccurredOn).TotalSeconds, 0));

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
            return (new OutboxCycleResult(HasMoreEligibleWork: false, earliestPending), pendingDepth);
        }

        var toProcess = await ClaimEligibleAsync(context, messages, eligibleCount, now, cancellationToken)
            .ConfigureAwait(false);

        if (toProcess.Count == 0)
        {
            // Another replica claimed the whole prefix between fetch and claim.
            return (new OutboxCycleResult(HasMoreEligibleWork: false, earliestPending), pendingDepth);
        }

        LogProcessingBatch(logger, toProcess.Count, sourceName);

        bool processedAny;
        try
        {
            processedAny = await DispatchMessagesAsync(
                toProcess, source, dispatcher, messageBus, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown landed mid-batch. Every message dispatched before the cancellation carries
            // its ProcessedOn stamp in the change tracker only, and the batch save below is now
            // unreachable, so without this the delivered messages stay unprocessed and are
            // redelivered once their lease expires (LeaseSeconds, 300s by default). Persisting the
            // stamps on the way out shrinks that duplicate window to nothing on a graceful
            // shutdown; delivery stays at-least-once for an ungraceful one.
            await TryPersistStampsOnCancellationAsync(context, sourceName).ConfigureAwait(false);
            throw;
        }

        // Plain DbContext.SaveChangesAsync, without a user id: the audit interceptor stamps its
        // system sentinel rather than a caller's identity. The EF interceptors still run (they are
        // registered on the context, not selected per call), but there is nothing for them to do
        // here: OutboxMessage is not an aggregate root, so no events are captured.
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // A full eligible batch with progress means more eligible rows may be waiting; the
        // progress requirement stops a fully-failing batch from hot-spinning the processor.
        return (
            new OutboxCycleResult(
                HasMoreEligibleWork: eligibleCount == _settings.BatchSize && processedAny,
                earliestPending),
            pendingDepth);
    }

    /// <summary>
    /// Backlog depth for one source, derived from the fetch wherever it can be: a batch that came
    /// back short IS the whole backlog, so the steady state costs nothing extra. Only a saturated
    /// batch (exactly the state an operator alerts on) pays for a COUNT, and that query runs inside
    /// its own <c>OutboxPoll</c> activity so OutboxPollFilterProcessor suppresses it from export
    /// exactly like the poll itself. The predicate mirrors <see cref="FetchCandidatesAsync"/> so
    /// the gauge counts the rows this processor considers workable.
    /// </summary>
    private async Task<long> CountPendingAsync(
        ApplicationDbContext context,
        string sourceName,
        int fetchedCount,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (fetchedCount < _settings.BatchSize)
        {
            return fetchedCount;
        }

        using var pollActivity = OutboxActivitySource.StartActivity(PollActivityName);
        pollActivity?.SetTag("messaging.outbox.data_source", sourceName);

        return await context.Set<OutboxMessage>()
            .Where(m => m.ProcessedOn == null
                && m.RetryCount < _settings.MaxRetries
                && (m.LockedUntil == null || m.LockedUntil < now))
            .LongCountAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Best-effort save of the ProcessedOn stamps collected before a cancellation, run on the way
    /// out of a cancelled batch. Two deliberate constraints:
    /// <list type="bullet">
    ///   <item>Its own try/catch. A failure here must never replace the propagating
    ///   <see cref="OperationCanceledException"/>: the loop in <c>ExecuteAsync</c> recognizes
    ///   shutdown by that exception type, and swapping it for a save failure would turn a clean
    ///   stop into a logged error plus another polling cycle.</item>
    ///   <item>Its own short-lived token rather than <see cref="CancellationToken.None"/>. The
    ///   caller's token is already cancelled, so it cannot be reused, but an uncancellable save
    ///   against a dead connection would hold host shutdown open until the command timeout.</item>
    /// </list>
    /// </summary>
    private async Task TryPersistStampsOnCancellationAsync(ApplicationDbContext context, string sourceName)
    {
        using var timeout = new CancellationTokenSource(ShutdownSaveTimeout, _timeProvider);

        try
        {
            await context.SaveChangesAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogShutdownSaveFailed(logger, sourceName, ex);
        }
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
            .ThenBy(m => m.Id)
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
    /// <para>
    /// Ordered delivery is enforced HERE rather than after the fetch, so it survives batching and
    /// scale-out: the claim predicate refuses a row carrying an <c>OrderingKey</c> while any earlier
    /// unprocessed, non-dead-lettered row shares that key (the <c>NOT EXISTS</c> below), and
    /// <see cref="SelectOrderedCandidates"/> keeps at most one row per key in this cycle's own
    /// candidate set. A predecessor still counts while it is retrying, which is the head-of-line
    /// blocking documented on <see cref="IHasOrderingKey"/>; once it exhausts its retries it stops
    /// blocking, so a poison event cannot freeze its key forever.
    /// </para>
    /// </summary>
    /// <remarks>
    /// The predecessor test is on <c>OccurredOn</c> alone. Two rows sharing a key AND an exact
    /// timestamp are ordered by <c>Id</c> within a cycle (the fetch orders by both), but neither
    /// blocks the other in SQL, because <see cref="Guid"/> has no order that both .NET and every
    /// provider agree on. A tie at tick resolution is not an ordering the outbox claims to observe.
    /// </remarks>
    private async Task<List<OutboxMessage>> ClaimEligibleAsync(
        ApplicationDbContext context,
        List<OutboxMessage> messages,
        int eligibleCount,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var lockToken = Guid.NewGuid();
        var leaseUntil = now.AddSeconds(_settings.LeaseSeconds);
        var candidates = SelectOrderedCandidates(messages, eligibleCount);
        if (candidates.Count == 0)
            return [];

        var eligibleIds = candidates.Select(m => m.Id).ToArray();
        var outbox = context.Set<OutboxMessage>();

        // A batch with no keyed row runs exactly the query it always ran: hosts that never declare
        // an ordering key pay nothing for the feature, not even a subquery the optimizer has to
        // prove away.
        var claim = candidates.Exists(m => m.OrderingKey is not null)
            ? FilterUnblocked(outbox, eligibleIds, now, _settings.MaxRetries)
            : FilterClaimable(outbox, eligibleIds, now);

        var claimedCount = await claim
            .ExecuteUpdateAsync(
                s => s.SetProperty(m => m.LockedUntil, leaseUntil).SetProperty(m => m.LockToken, lockToken),
                cancellationToken)
            .ConfigureAwait(false);

        if (claimedCount == 0)
            return [];

        if (claimedCount == eligibleIds.Length)
            return candidates;

        // Partial claim: process only the rows carrying this replica's token.
        var claimedIds = await outbox.AsNoTracking()
            .Where(m => eligibleIds.Contains(m.Id) && m.LockToken == lockToken)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var claimedSet = claimedIds.ToHashSet();
        return [.. candidates.Where(m => claimedSet.Contains(m.Id))];
    }

    /// <summary>
    /// Narrows the eligible prefix to the rows this cycle may attempt: every unkeyed row, plus the
    /// FIRST row of each ordering key. The batch is already sorted by <c>OccurredOn</c> then
    /// <c>Id</c>, so "first" is the earliest, and dropping its key-mates here is what keeps a single
    /// cycle from dispatching two events of one key in parallel. Their turn comes on a later cycle,
    /// once this row is processed and stops satisfying the claim's predecessor test.
    /// </summary>
    private static List<OutboxMessage> SelectOrderedCandidates(List<OutboxMessage> messages, int eligibleCount)
    {
        List<OutboxMessage> candidates = [];
        var keysTaken = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < eligibleCount; i++)
        {
            var message = messages[i];

            if (message.OrderingKey is { } key && !keysTaken.Add(key))
                continue;

            candidates.Add(message);
        }

        return candidates;
    }

    /// <summary>
    /// The claim predicate every batch shares: these ids, still unprocessed, not under another
    /// replica's unexpired lease.
    /// </summary>
    private static IQueryable<OutboxMessage> FilterClaimable(
        IQueryable<OutboxMessage> outbox,
        Guid[] eligibleIds,
        DateTime now) =>
        outbox.Where(m => eligibleIds.Contains(m.Id)
            && m.ProcessedOn == null
            && (m.LockedUntil == null || m.LockedUntil < now));

    /// <summary>
    /// The claim predicate plus the ordering guard: a keyed row is refused while any EARLIER
    /// unprocessed, non-dead-lettered row shares its key. Expressed as a correlated <c>NOT EXISTS</c>
    /// inside the claim itself, so the guard is evaluated by the database at the instant of the
    /// update: a second replica racing the same key loses on the row rather than on a check it made
    /// before the race started.
    /// </summary>
    private static IQueryable<OutboxMessage> FilterUnblocked(
        IQueryable<OutboxMessage> outbox,
        Guid[] eligibleIds,
        DateTime now,
        int maxRetries) =>
        FilterClaimable(outbox, eligibleIds, now)
            .Where(m => m.OrderingKey == null
                || !outbox.Any(p => p.OrderingKey == m.OrderingKey
                    && p.ProcessedOn == null
                    && p.RetryCount < maxRetries
                    && p.OccurredOn < m.OccurredOn));

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

        // Log-once latch for this batch: an open circuit rejects every remaining row in the same
        // instant, and 50 identical Warning lines per cycle is noise an operator learns to filter.
        // The per-row signal stays on the metric (BrokerMetrics.CircuitOpenCounter).
        var circuitOpenLogged = false;

        foreach (var message in messages)
        {
            using var activity = StartOutboxActivity(message, source);
            try
            {
                var domainEvent = message.DeserializeEvent();
                if (domainEvent is null)
                {
                    processedAny |= HandleUnresolvableType(message);
                    continue;
                }

                // Integration events route through IMessageBus so the registered transport
                // (in-process for the monolith, MassTransit broker for extracted services)
                // determines delivery. Pure domain events keep the legacy in-process dispatch.
                if (domainEvent is IIntegrationEvent integrationEvent)
                {
                    // Only the broker hop is wrapped. The in-process dispatcher branch below is a
                    // direct method call into this same process: it has no transport to be dead,
                    // so a breaker there would only add a way to reject work that would have
                    // succeeded.
                    await _brokerPublishPipeline.ExecuteAsync(
                        static async (state, ct) =>
                            await state.Bus.PublishAsync(state.Event, ct).ConfigureAwait(false),
                        (Bus: messageBus, Event: integrationEvent),
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await dispatcher.DispatchAsync([domainEvent], cancellationToken).ConfigureAwait(false);
                }

                var processedOn = _timeProvider.GetUtcNow().UtcDateTime;
                message.ProcessedOn = processedOn;
                processedAny = true;

                var eventTypeTag = new KeyValuePair<string, object?>("event_type", message.EventType);
                OutboxMetrics.ProcessedCounter.Add(1, eventTypeTag);

                // End-to-end delivery lag in seconds. Clamped at zero: OccurredOn is stamped by the
                // writing host and ProcessedOn by this one, so clock skew between them must not
                // publish a negative duration into the histogram.
                OutboxMetrics.DispatchLagHistogram.Record(
                    Math.Max((processedOn - message.OccurredOn).TotalSeconds, 0),
                    eventTypeTag);

                LogMessageProcessed(logger, message.Id, message.EventType);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Host shutdown, not a delivery failure. Falling into the generic handler below
                // would increment RetryCount and stamp LastError on this message and, since every
                // later await fails the same way, on the whole remainder of the batch: a graceful
                // restart could dead-letter messages that were never actually attempted.
                throw;
            }
            catch (Exception ex)
            {
                message.RetryCount++;
                message.LastError = ex.Message;

                // Re-lease the row for an explicit backoff instead of leaving this cycle's claim on
                // it. The claim is not cleared outright: the fetch skips leased rows, so a failure
                // that kept the original lease was retried only after the full LeaseSeconds (300s by
                // default) no matter what the polling interval or a signal said. That made the retry
                // cadence an accident of the lease. Capping at the lease keeps a permanently failing
                // message from becoming unclaimable for longer than a dead replica's rows would.
                message.LockedUntil = _timeProvider.GetUtcNow().UtcDateTime
                    .AddSeconds(ComputeRetryBackoffSeconds(message.RetryCount));

                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

                // An open circuit is a rejection, not a delivery attempt: the publish never left
                // the process. It still follows the normal failure path above (retry increment and
                // re-lease) so the row is retried on a later cycle exactly like any other failure,
                // but it gets its own counter and its own log line, because "the broker refused
                // 50 messages" and "we did not try, the broker is known-dead" are different
                // operational facts.
                var circuitOpen = ex is BrokenCircuitException;
                if (circuitOpen)
                {
                    BrokerMetrics.CircuitOpenCounter.Add(
                        1,
                        new KeyValuePair<string, object?>("event_type", message.EventType));
                }

                if (circuitOpen && !circuitOpenLogged)
                {
                    circuitOpenLogged = true;
                    LogBrokerCircuitOpen(logger, source.ToString());
                }

                if (message.RetryCount >= _settings.MaxRetries)
                {
                    // The moment of exhaustion is the operator's last loud signal: from here the
                    // row leaves the poll (RetryCount filter) and is eventually purged by
                    // OutboxCleanupService after the dead-letter retention window.
                    OutboxMetrics.DeadLetterCounter.Add(
                        1,
                        new KeyValuePair<string, object?>("event_type", message.EventType),
                        new KeyValuePair<string, object?>("reason", "retries_exhausted"));
                    LogRetriesExhausted(logger, message.Id, message.EventType, message.RetryCount, ex);
                }
                else if (!circuitOpen)
                {
                    // Circuit-open rejections already reported themselves above, once per batch.
                    LogMessageRetry(logger, message.Id, message.RetryCount, ex);
                }
            }
        }

        return processedAny;
    }

    /// <summary>
    /// Handles a row whose stored <c>EventType</c> resolved to nothing. The FIRST such attempt is
    /// treated as transient and retried through the normal backoff path: the assembly declaring the
    /// type may simply not be loaded yet (a module assembly resolved lazily, a host still coming up),
    /// and a name that resolves one cycle later was never a dead letter. Only the second attempt is
    /// terminal, which is also the point at which an operator has had a Warning naming the row.
    /// </summary>
    /// <param name="message">The row that could not be deserialized.</param>
    /// <returns>
    /// <see langword="true"/> when the row reached a terminal state this cycle (progress),
    /// <see langword="false"/> when it was merely scheduled for one more attempt.
    /// </returns>
    private bool HandleUnresolvableType(OutboxMessage message)
    {
        message.LastError = $"Cannot resolve type: {message.EventType}";

        // MaxRetries of 1 means the host asked for no retries at all; honor that rather than
        // scheduling an attempt the poll's RetryCount filter would never pick up again.
        if (message.RetryCount == 0 && _settings.MaxRetries > 1)
        {
            message.RetryCount++;
            message.LockedUntil = _timeProvider.GetUtcNow().UtcDateTime
                .AddSeconds(ComputeRetryBackoffSeconds(message.RetryCount));
            LogTypeUnresolvableRetry(logger, message.Id, message.EventType);
            return false;
        }

        message.ProcessedOn = _timeProvider.GetUtcNow().UtcDateTime;
        OutboxMetrics.DeadLetterCounter.Add(
            1,
            new KeyValuePair<string, object?>("event_type", message.EventType),
            new KeyValuePair<string, object?>("reason", "type_unresolvable"));
        LogDeadLetter(logger, message.Id, message.EventType);
        return true;
    }

    /// <summary>
    /// Exponential backoff for a failed message: <c>base * 2^(retryCount - 1)</c>, multiplied by a
    /// random jitter factor in <c>[0.8, 1.2]</c> and then capped at the lease so a failing row never
    /// holds its claim longer than a dead replica's rows would. The jitter is what keeps a batch
    /// that failed together (one dependency outage fails all 50 rows in the same instant) from
    /// retrying in lockstep and re-hammering that dependency on a single shared schedule.
    /// </summary>
    internal double ComputeRetryBackoffSeconds(int retryCount)
    {
        // Clamp the shift exponent before it reaches Math.Pow: MaxRetries is bounded at 20 today,
        // but the cap below is what actually decides the wait, so there is no reason to let a
        // future settings change turn this into an overflow.
        var exponent = Math.Min(Math.Max(retryCount - 1, 0), 16);
        var backoff = _settings.RetryBackoffBaseSeconds * Math.Pow(2, exponent);

        // Jitter is applied BEFORE the cap so a capped backoff stays exactly at the lease bound.
#pragma warning disable S2245, CA5394 // Random spaces retry attempts apart (jitter); it feeds no security, token, key or cryptographic decision, so a pseudorandom generator is the correct tool here.
        var jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
#pragma warning restore S2245, CA5394

        return Math.Min(backoff * jitter, _settings.LeaseSeconds);
    }

    /// <summary>
    /// Builds the broker-publish circuit breaker from <see cref="BrokerResilienceDefaults"/>.
    /// <see cref="OperationCanceledException"/> is excluded from the handled set: a host shutdown
    /// cancelling a batch mid-flight is not evidence that the broker is unhealthy, and letting it
    /// count toward the failure ratio would leave the circuit open against a perfectly good broker
    /// on the next start.
    /// </summary>
    private static ResiliencePipeline BuildBrokerPublishPipeline() =>
        new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = BrokerResilienceDefaults.FailureRatio,
                MinimumThroughput = BrokerResilienceDefaults.MinimumThroughput,
                SamplingDuration = BrokerResilienceDefaults.SamplingDuration,
                BreakDuration = BrokerResilienceDefaults.BreakDuration,
                ShouldHandle = new PredicateBuilder()
                    .Handle<Exception>(ex => ex is not OperationCanceledException),
            })
            .Build();

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox shutdown save failed for data source {DataSourceName}: messages delivered in the cancelled batch will be redelivered when their lease expires")]
    private static partial void LogShutdownSaveFailed(ILogger logger, string dataSourceName, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Processing {Count} pending outbox messages from {DataSourceName}")]
    private static partial void LogProcessingBatch(ILogger logger, int count, string dataSourceName);

    // Debug, not Information: this fires once per dispatched message and would otherwise be the
    // single noisiest log line in steady state — a real telemetry-ingestion cost (rubric §31, the published COST guide).
    // Failures stay loud (dead-letter = Error, retry = Warning); success detail is Debug.
    [LoggerMessage(Level = LogLevel.Debug, Message = "Outbox message {MessageId} ({EventType}) dispatched successfully")]
    private static partial void LogMessageProcessed(ILogger logger, Guid messageId, string eventType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {MessageId} dead-lettered: type not resolvable — {EventType}")]
    private static partial void LogDeadLetter(ILogger logger, Guid messageId, string eventType);

    // Warning, not Error: one unresolved attempt is a maybe (the declaring assembly may load on a
    // later cycle), and the terminal attempt logs at Error above. Names the fix that prevents the
    // next occurrence, which is a one-line change on the event itself.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} could not resolve event type {EventType}; retrying once before dead-lettering. Give the event an [EventName] so its rows carry an identity a rename, namespace move, or assembly move cannot break")]
    private static partial void LogTypeUnresolvableRetry(ILogger logger, Guid messageId, string eventType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} failed (attempt {RetryCount})")]
    private static partial void LogMessageRetry(ILogger logger, Guid messageId, int retryCount, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {MessageId} ({EventType}) dead-lettered: retries exhausted after {RetryCount} attempts — the event was never delivered")]
    private static partial void LogRetriesExhausted(ILogger logger, Guid messageId, string eventType, int retryCount, Exception exception);

    // Logged once per batch, not once per message: an open circuit rejects every remaining row in
    // the same instant. Warning rather than Error because nothing is lost, only deferred.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Broker circuit is open for data source {DataSourceName}: skipping outbox publishes this cycle and retrying the affected messages on a later one")]
    private static partial void LogBrokerCircuitOpen(ILogger logger, string dataSourceName);
}

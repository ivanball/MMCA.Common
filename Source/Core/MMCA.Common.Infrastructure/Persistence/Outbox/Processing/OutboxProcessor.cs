using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Persistence.Conversions;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Outbox.Administration;
using MMCA.Common.Infrastructure.Persistence.Polling;
using MMCA.Common.Shared.Resilience;
using Polly;
using Polly.CircuitBreaker;

namespace MMCA.Common.Infrastructure.Persistence.Outbox.Processing;

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
/// Delivery is at-least-once. A failed row's retry or dead-letter state is written as soon as that
/// row is done; delivered rows are buffered and stamped processed together, by one set-based update
/// per flush, at the end of the batch, before any lease renewal, and on every early exit (a
/// shutdown, a failed write). Every write is guarded on this replica's lease token, and the lease is
/// renewed for the rest of the batch, in one statement, once less than half of it remains. So a
/// batch that outlives its lease never has a row another replica has taken over stamped by the
/// replica that lost it, and a row found lost at a renewal is not dispatched again. A shutdown
/// signalled while a row is in flight still records the outcomes of the rows already done, under
/// its own short budget, before the shutdown continues. The rows delivered since the last flush are
/// redelivered after a crash (or when the exit flush fails), once their lease expires after
/// <c>Outbox:LeaseSeconds</c> (300s by default), because the poll skips leased rows.
/// </para>
/// </summary>
/// <param name="scopeFactory">Factory for creating DI scopes per processing cycle.</param>
/// <param name="logger">Logger for processing diagnostics.</param>
/// <param name="outboxOptions">Configurable outbox processing settings.</param>
/// <param name="outboxSignal">Signal to wait on between polling cycles for immediate wakeup.</param>
/// <param name="tableTargets">
/// Decides which databases hold an outbox table this host drains, including each tenant that keeps
/// its own copy of a source (whose outbox nothing else would drain).
/// </param>
/// <param name="timeProvider">Clock abstraction for the startup delay and lease/eligibility timestamps;
/// injected so tests can drive the loop deterministically.</param>
public sealed partial class OutboxProcessor(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxProcessor> logger,
    IOptions<OutboxSettings> outboxOptions,
    IOutboxSignal outboxSignal,
    FrameworkTableTargets tableTargets,
    TimeProvider timeProvider) : BackgroundService
{
    private readonly OutboxSettings _settings = outboxOptions.Value;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <summary>
    /// Name of the per-cycle poll activity wrapping the outbox fetch query. Must stay in sync
    /// with <c>OutboxPollFilterProcessor</c> in MMCA.Common.Aspire, which suppresses these spans
    /// and their SqlClient children from telemetry export (Aspire has no project references, so
    /// the string is deliberately duplicated there).
    /// </summary>
    internal const string PollActivityName = "OutboxPoll";

    /// <summary>
    /// Authentication type stamped on the identity rebuilt from a row before delivery. It is what
    /// makes <c>IsAuthenticated</c> true, and it names the hop the identity came back from.
    /// </summary>
    internal const string OutboxPrincipalAuthenticationType = "Outbox";

    /// <summary>Width of the <c>LastError</c> column; longer failure text is truncated to fit (the siblings' constant).</summary>
    private const int MaxErrorLength = 4000;

    /// <summary>
    /// Most ids one batched stamp or renewal statement carries, well under SQL Server's 2100-parameter
    /// limit; a larger <c>Outbox:BatchSize</c> is written in chunks of this size.
    /// </summary>
    private const int MaxIdsPerStatement = 500;

    /// <summary>
    /// Budget for the writes made on an early exit from a batch: the flush of the buffered delivered
    /// rows (<see cref="FlushDeliveredOnExitAsync"/>) and the retry state of a row that FAILED while the
    /// host was shutting down (<see cref="RecordFailureOnShutdownAsync"/>). Deliberately short: the work
    /// is a few small UPDATEs against an already-open connection, and anything slower is a dependency
    /// that must not delay host shutdown.
    /// </summary>
    private static readonly TimeSpan ShutdownStampTimeout = TimeSpan.FromSeconds(5);

    private static readonly ActivitySource OutboxActivitySource = new("MMCA.Common.Outbox");

    /// <summary>
    /// Circuit breaker guarding the integration-event <see cref="IMessageBus"/> publish call only:
    /// the broker hop on a host that registers broker messaging, and the in-process handler
    /// dispatch on a monolith, where the default <c>InProcessMessageBus</c> calls
    /// <see cref="IDomainEventDispatcher"/> inside the breaker (never the database calls: a breaker
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
    /// <remarks>The loop, startup delay and smart wait are the shared <see cref="PollingLoop"/>.</remarks>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        PollingLoop.RunAsync(
            _timeProvider,
            () => GetOutboxTargets().Count > 0,
            () => LogOutboxDisabled(logger),
            async ct =>
            {
                var cycle = await ProcessPendingMessagesAsync(ct).ConfigureAwait(false);
                return (cycle.HasMoreEligibleWork, cycle.EarliestPendingOccurredOn);
            },
            ex => LogProcessingError(logger, ex),
            _settings.ProcessingDelaySeconds,
            _settings.PollingIntervalSeconds,
            outboxSignal.WaitAsync,
            stoppingToken);

    /// <summary>
    /// Computes how long to wait before the next polling cycle: until the earliest pending message
    /// becomes eligible, capped at the polling interval and floored at one second (see
    /// <see cref="PollingLoop.ComputeWaitTime"/>).
    /// </summary>
    internal static TimeSpan ComputeWaitTime(
        DateTime? earliestPendingOccurredOn,
        DateTime utcNow,
        TimeSpan processingDelay,
        TimeSpan pollingInterval) =>
        PollingLoop.ComputeWaitTime(earliestPendingOccurredOn, utcNow, processingDelay, pollingInterval);

    /// <summary>
    /// The units this cycle visits: every relational source this host owns (every source backing a
    /// registered entity plus the configured publish target; Cosmos has no outbox table) against the
    /// shared database, plus one extra unit per tenant that keeps its own copy of a source. A tenant
    /// database has its own <c>OutboxMessages</c> table, and nothing else opens that database, so
    /// without this its events would sit undelivered forever.
    /// </summary>
    internal IReadOnlyList<TenantDataSourceTarget> GetOutboxTargets() =>
        tableTargets.Relational(_settings.DataSource, _settings.DatabaseName);

    /// <summary>
    /// Drains every outbox source once and aggregates the per-source results: any source with
    /// more eligible work triggers an immediate re-poll, the earliest pending timestamp
    /// across all sources drives the smart wait, and the backlog observed across all sources is
    /// published to the <c>outbox.pending.depth</c> gauge.
    /// </summary>
    internal async Task<OutboxCycleResult> ProcessPendingMessagesAsync(CancellationToken cancellationToken)
    {
        var (hasMoreEligibleWork, earliestPendingOccurredOn, pendingDepth) = await PollingLoop.DrainAllAsync(
            GetOutboxTargets(),
            async (target, ct) =>
            {
                (OutboxCycleResult result, long sourcePendingDepth) =
                    await ProcessSourceAsync(target, ct).ConfigureAwait(false);
                return (result.HasMoreEligibleWork, result.EarliestPendingOccurredOn, sourcePendingDepth);
            },
            (sourceName, ex) => LogSourceProcessingError(logger, sourceName, ex),
            cancellationToken).ConfigureAwait(false);

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
        // The tenant is set before the context is asked for (see CreateTenantScope).
        using var scope = scopeFactory.CreateTenantScope(target);

        var dbContextFactory = scope.ServiceProvider.GetRequiredService<DbContexts.Factory.IDbContextFactory>();
        var context = dbContextFactory.GetDbContext(source);

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

        var lockToken = Guid.NewGuid();
        var leaseUntil = now.AddSeconds(_settings.LeaseSeconds);
        var (toProcess, deferredKeyMates) = await ClaimEligibleAsync(context, messages, eligibleCount, now, leaseUntil, lockToken, cancellationToken)
            .ConfigureAwait(false);

        if (toProcess.Count == 0)
        {
            // Another replica claimed the whole prefix between fetch and claim.
            return (new OutboxCycleResult(HasMoreEligibleWork: false, earliestPending), pendingDepth);
        }

        LogProcessingBatch(logger, toProcess.Count, sourceName);

        // Every outcome is persisted by DispatchMessagesAsync itself, through guarded set-based
        // updates (failures per row, deliveries per flush), so there is no batch save here: a
        // cancellation partway through still flushes the rows already delivered to the database
        // rather than leaving them only in the change tracker.
        var processedAny = await DispatchMessagesAsync(context, toProcess, target, lockToken, leaseUntil, cancellationToken)
            .ConfigureAwait(false);

        // A full eligible batch with progress means more eligible rows may be waiting, and so do
        // key-mates this cycle deferred behind their key's head row: once that row is delivered
        // they are claimable at once, and waiting out the polling interval (300s in deployed
        // environments) for each successor would serialize a key at one row per poll. The progress
        // requirement stops a fully-failing batch from hot-spinning the processor.
        return (
            new OutboxCycleResult(
                HasMoreEligibleWork: processedAny && (eligibleCount == _settings.BatchSize || deferredKeyMates),
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
    private async Task<(List<OutboxMessage> Claimed, bool DeferredKeyMates)> ClaimEligibleAsync(
        ApplicationDbContext context,
        List<OutboxMessage> messages,
        int eligibleCount,
        DateTime now,
        DateTime leaseUntil,
        Guid lockToken,
        CancellationToken cancellationToken)
    {
        var candidates = SelectOrderedCandidates(messages, eligibleCount);

        // Key-mates dropped by SelectOrderedCandidates wait only for this cycle's head row, so the
        // caller asks for an immediate re-poll when it delivers anything.
        var deferredKeyMates = candidates.Count < eligibleCount;
        if (candidates.Count == 0)
            return ([], deferredKeyMates);

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
            return ([], deferredKeyMates);

        if (claimedCount == eligibleIds.Length)
            return (candidates, deferredKeyMates);

        // Partial claim: process only the rows carrying this replica's token.
        var claimedIds = await outbox.AsNoTracking()
            .Where(m => eligibleIds.Contains(m.Id) && m.LockToken == lockToken)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var claimedSet = claimedIds.ToHashSet();
        return ([.. candidates.Where(m => claimedSet.Contains(m.Id))], deferredKeyMates);
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
    /// Dispatches each eligible message, marking successes as processed, dead-lettering terminal
    /// failures (retry budget spent, <c>ProcessedOn</c> left null) and incrementing retry counts on
    /// failure. Returns whether any message made progress
    /// (dispatched or dead-lettered) this cycle.
    /// </summary>
    /// <remarks>
    /// Each row's captured context (user, roles, tenant, correlation id) is restored onto a FRESH
    /// scope per row, created for the batch's target, BEFORE the row is published or dispatched, so
    /// one row's identity can never answer for another's. A fresh scope is what lets the tenant
    /// change between rows: the tenant context refuses a change once resolved, so on one shared
    /// scope every later row of a shared-target batch ran under the first row's tenant. The row
    /// scope carries the original request's identity across the hop: <c>BrokerMessageBus</c> reads
    /// the restored values through the row scope's services when it stamps its headers, and the
    /// in-process path (<c>InProcessMessageBus</c> to <see cref="IDomainEventDispatcher"/>) gets the
    /// right ambient context for free. The cycle scope's context holds the claimed rows and writes
    /// the lease renewals and outcomes; only delivery moves to the row scope.
    /// <para>
    /// The claim lease covers the whole batch, so a slow batch could outlive it and let another
    /// replica claim a later row while this one is still busy. Before each row is dispatched the
    /// remaining lease is checked against the clock, and once less than half of
    /// <c>Outbox:LeaseSeconds</c> is left the buffered stamps are flushed and every row of the batch
    /// not yet dispatched is renewed in one statement guarded on this batch's lock token. A row that
    /// renewal finds under another token is skipped. A lease that lapses during one long dispatch is
    /// therefore caught by the next check, which renews or skips every remaining row before another
    /// goes out.
    /// </para>
    /// <para>
    /// A failed row's retry state is written the guarded way as soon as the row is done. Delivered
    /// rows are buffered and stamped processed by one guarded set-based update per flush: at the end
    /// of the batch, before a lease renewal, and on every early exit (under
    /// <see cref="ShutdownStampTimeout"/>), so a replica that lost a row mid-dispatch drops its stale
    /// stamp instead of overwriting the new owner's, and a shutdown does not cost the rows already
    /// delivered their stamps.
    /// </para>
    /// </remarks>
    /// <param name="context">The cycle scope's context holding the claimed rows.</param>
    /// <param name="messages">The claimed rows to deliver.</param>
    /// <param name="target">The target the batch was claimed from; each row scope is created for it.</param>
    /// <param name="lockToken">This batch's claim token, which every renewal and stamp is guarded by.</param>
    /// <param name="claimLeaseUntil">The lease expiry the claim wrote for this batch.</param>
    /// <param name="cancellationToken">Cancels the batch.</param>
    private async Task<bool> DispatchMessagesAsync(
        ApplicationDbContext context,
        List<OutboxMessage> messages,
        TenantDataSourceTarget target,
        Guid lockToken,
        DateTime claimLeaseUntil,
        CancellationToken cancellationToken)
    {
        var batch = new ClaimedBatch(messages, lockToken, claimLeaseUntil);
        var batchCompleted = false;

        try
        {
            var processedAny = await DispatchRowsAsync(context, batch, target, cancellationToken).ConfigureAwait(false);
            await FlushDeliveredAsync(context, batch, cancellationToken).ConfigureAwait(false);
            batchCompleted = true;
            return processedAny;
        }
        finally
        {
            if (!batchCompleted)
            {
                await FlushDeliveredOnExitAsync(context, batch).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Dispatches the rows of <paramref name="batch"/> in order (see <see cref="DispatchMessagesAsync"/>):
    /// renews the lease when it is due, skips a row the renewal found lost, delivers each row on its
    /// own scope, then buffers a delivered row for the batched stamp or writes a failed row's retry
    /// state. Returns whether any row made progress.
    /// </summary>
    private async Task<bool> DispatchRowsAsync(
        ApplicationDbContext context,
        ClaimedBatch batch,
        TenantDataSourceTarget target,
        CancellationToken cancellationToken)
    {
        var source = target.Source;
        var processedAny = false;

        // Log-once latch for this batch: an open circuit rejects every remaining row in the same
        // instant, and 50 identical Warning lines per cycle is noise an operator learns to filter.
        // The per-row signal stays on the metric (BrokerMetrics.CircuitOpenCounter).
        var circuitOpenLogged = false;

        for (var index = 0; index < batch.Messages.Count; index++)
        {
            var message = batch.Messages[index];

            if (!await RenewLeaseIfDueAsync(context, batch, index, cancellationToken).ConfigureAwait(false))
            {
                LogLeaseLostBeforeDispatch(logger, message.Id);
                continue;
            }

            using var activity = StartOutboxActivity(message, source);

            var delivered = false;

            // Tenant-owned targets keep their tenant; the shared target starts unresolved, so the
            // restore below can set this row's tenant (or leave it unset for a tenantless row). The
            // scope only carries the delivery, so it is disposed before the outcome is written
            // through the cycle context.
            using (var rowScope = scopeFactory.CreateTenantScope(target))
            {
                try
                {
                    // Before anything reads the scope: the publish path stamps headers from these
                    // services and the in-process path hands them to the handlers. The handle keeps the
                    // origin published for scopes the handlers open themselves, until this row is done.
                    using var origin = Context.AmbientOrigin.Restore(
                        rowScope.ServiceProvider,
                        message.UserId,
                        message.UserRoles,
                        message.UserPermissions,
                        message.TenantId,
                        message.CorrelationId,
                        OutboxPrincipalAuthenticationType);

                    var domainEvent = message.DeserializeEvent();
                    if (domainEvent is null)
                    {
                        processedAny |= HandleUnresolvableType(message);
                    }
                    else
                    {
                        await DeliverAsync(domainEvent, rowScope.ServiceProvider, cancellationToken).ConfigureAwait(false);
                        delivered = true;
                    }
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
                    message.LastError = ColumnWidth.Truncate(ex.Message, MaxErrorLength);

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

            // Outside the try on purpose: a database failure while recording the outcome is not a
            // delivery failure, so it must not be charged to the row as a retry. It propagates and
            // fails the source for this cycle. A delivered row only joins the stamp buffer; a failed
            // row's retry state is written now, and a shutdown signalled mid-delivery does not skip
            // that write (see RecordFailureOutcomeAsync).
            processedAny |= delivered;
            await RecordOutcomeAsync(context, batch, message, delivered, cancellationToken).ConfigureAwait(false);

            // A shutdown signalled while this row was in flight stops the batch here, after the row's
            // outcome is recorded; DispatchMessagesAsync then stamps the rows already delivered.
            cancellationToken.ThrowIfCancellationRequested();
        }

        return processedAny;
    }

    /// <summary>
    /// Checks this replica's lease before the row at <paramref name="index"/> is dispatched. While
    /// at least half of <c>Outbox:LeaseSeconds</c> remains (by the injected clock) nothing is
    /// written. Once less remains, the buffered stamps are flushed and every row not yet dispatched
    /// is renewed (<see cref="RenewRemainingLeasesAsync"/>). Returns whether the row is still this
    /// replica's, so a row a renewal found under another token is never dispatched.
    /// </summary>
    private async Task<bool> RenewLeaseIfDueAsync(
        ApplicationDbContext context,
        ClaimedBatch batch,
        int index,
        CancellationToken cancellationToken)
    {
        var remaining = batch.LeaseUntil - _timeProvider.GetUtcNow().UtcDateTime;
        if (remaining < TimeSpan.FromSeconds(_settings.LeaseSeconds / 2.0))
        {
            await FlushDeliveredAsync(context, batch, cancellationToken).ConfigureAwait(false);
            await RenewRemainingLeasesAsync(context, batch, index, cancellationToken).ConfigureAwait(false);
        }

        return !batch.LostIds.Contains(batch.Messages[index].Id);
    }

    /// <summary>
    /// Records one row's outcome: a delivered row joins the batch's stamp buffer (written by
    /// <see cref="FlushDeliveredAsync"/>), a failed row's retry state is written now
    /// (<see cref="RecordFailureOutcomeAsync"/>).
    /// </summary>
    private Task RecordOutcomeAsync(
        ApplicationDbContext context,
        ClaimedBatch batch,
        OutboxMessage message,
        bool delivered,
        CancellationToken cancellationToken)
    {
        if (!delivered)
        {
            return RecordFailureOutcomeAsync(context, message, batch.LockToken, cancellationToken);
        }

        batch.PendingStamps.Add(message);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Persists one failed row's retry state.
    /// <para>
    /// A shutdown signalled while the row was in flight must not cost the row its recorded attempt,
    /// so once the batch token is cancelled the outcome is written under its own short budget
    /// (<see cref="ShutdownStampTimeout"/>) instead, and the shutdown then continues by rethrowing
    /// the cancellation. A failure of that last write is logged rather than allowed to replace the
    /// propagating <see cref="OperationCanceledException"/> (the polling loop recognizes shutdown by
    /// that exception type), and the budget is short rather than <see cref="CancellationToken.None"/>,
    /// so a dead connection cannot hold host shutdown open until the command timeout. Without a
    /// shutdown a database failure propagates.
    /// </para>
    /// </summary>
    private async Task RecordFailureOutcomeAsync(
        ApplicationDbContext context,
        OutboxMessage message,
        Guid lockToken,
        CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RecordFailureAsync(context, message, lockToken, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The shutdown arrived during the write itself. The guarded update is idempotent, so
                // it is simply written again under the shutdown budget.
            }
        }

        await RecordFailureOnShutdownAsync(context, message, lockToken, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes one failed row's retry state under the shutdown budget, logging (never throwing) a
    /// failure so it cannot replace the cancellation, which is then rethrown so the shutdown continues.
    /// </summary>
    private async Task RecordFailureOnShutdownAsync(
        ApplicationDbContext context,
        OutboxMessage message,
        Guid lockToken,
        CancellationToken cancellationToken)
    {
        using var budget = new CancellationTokenSource(ShutdownStampTimeout, _timeProvider);
        try
        {
            await RecordFailureAsync(context, message, lockToken, budget.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogShutdownStampFailed(logger, message.Id, ex);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Delivers one deserialized event. Integration events route through <see cref="IMessageBus"/>
    /// so the registered transport (in-process for the monolith, MassTransit broker for extracted
    /// services) determines delivery; pure domain events keep the in-process dispatch.
    /// </summary>
    private async Task DeliverAsync(IDomainEvent domainEvent, IServiceProvider rowServices, CancellationToken cancellationToken)
    {
        if (domainEvent is IIntegrationEvent integrationEvent)
        {
            var messageBus = rowServices.GetRequiredService<IMessageBus>();

            // Only the IMessageBus publish is wrapped. With broker messaging that is the broker hop;
            // on a monolith the default InProcessMessageBus runs the integration-event handlers
            // in-process inside this breaker, so failing handlers can open it too. The pure
            // domain-event branch below is not wrapped: it is a direct method call into this same
            // process with no transport to be dead.
            await _brokerPublishPipeline.ExecuteAsync(
                static async (state, ct) =>
                    await state.Bus.PublishAsync(state.Event, ct).ConfigureAwait(false),
                (Bus: messageBus, Event: integrationEvent),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var dispatcher = rowServices.GetRequiredService<IDomainEventDispatcher>();
            await dispatcher.DispatchAsync([domainEvent], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Renews this replica's lease on every row of the batch from <paramref name="fromIndex"/> on that
    /// is not already known lost, in one statement guarded on the batch's lock token (per chunk of
    /// <see cref="MaxIdsPerStatement"/> ids), and records the renewed expiry on the batch. When fewer
    /// rows are renewed than asked for, one query finds the rows still carrying the token, and the
    /// rest join <see cref="ClaimedBatch.LostIds"/> so they are skipped rather than dispatched a
    /// second time.
    /// </summary>
    private async Task RenewRemainingLeasesAsync(
        ApplicationDbContext context,
        ClaimedBatch batch,
        int fromIndex,
        CancellationToken cancellationToken)
    {
        var leaseUntil = _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(_settings.LeaseSeconds);
        var lockToken = batch.LockToken;
        var lostIds = batch.LostIds;
        var outbox = context.Set<OutboxMessage>();

        Guid[] remainingIds = [.. batch.Messages
            .Skip(fromIndex)
            .Select(m => m.Id)
            .Where(id => !lostIds.Contains(id))];

        foreach (var ids in remainingIds.Chunk(MaxIdsPerStatement))
        {
            var renewed = await outbox
                .Where(m => ids.Contains(m.Id) && m.LockToken == lockToken)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.LockedUntil, leaseUntil), cancellationToken)
                .ConfigureAwait(false);

            if (renewed == ids.Length)
            {
                continue;
            }

            var keptIds = await outbox.AsNoTracking()
                .Where(m => ids.Contains(m.Id) && m.LockToken == lockToken)
                .Select(m => m.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var keptSet = keptIds.ToHashSet();
            lostIds.UnionWith(ids.Where(id => !keptSet.Contains(id)));
        }

        batch.LeaseUntil = leaseUntil;
    }

    /// <summary>
    /// Stamps the buffered delivered rows processed with one set-based update per chunk of
    /// <see cref="MaxIdsPerStatement"/> ids, guarded by the lock token and sharing one
    /// <c>ProcessedOn</c> value (the flush time). A row another replica has re-claimed under a new
    /// token (possible only after this replica's lease expired mid-dispatch) is not matched, so its
    /// stale stamp is dropped rather than overwriting the new owner's record. When fewer rows are
    /// stamped than buffered, one query finds the rows that carry the token. The tracked instances
    /// are then synced the way <see cref="StampAsync"/> syncs one row (stamped rows accepted, the
    /// rest detached), and the processed count, dispatch lag and success log are emitted for the
    /// stamped rows only. Each chunk leaves the buffer once synced, so a retried flush never stamps
    /// or counts a row twice.
    /// </summary>
    private async Task FlushDeliveredAsync(
        ApplicationDbContext context,
        ClaimedBatch batch,
        CancellationToken cancellationToken)
    {
        var pending = batch.PendingStamps;
        var lockToken = batch.LockToken;
        var outbox = context.Set<OutboxMessage>();

        while (pending.Count > 0)
        {
            var chunk = pending.GetRange(0, Math.Min(pending.Count, MaxIdsPerStatement));
            var ids = chunk.ConvertAll(m => m.Id);
            var processedOn = _timeProvider.GetUtcNow().UtcDateTime;

            var stamped = await outbox
                .Where(m => ids.Contains(m.Id) && m.LockToken == lockToken)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.ProcessedOn, processedOn), cancellationToken)
                .ConfigureAwait(false);

            // A stamped row no longer satisfies the claim predicate (ProcessedOn is set), so no other
            // replica can take it between the update and this read.
            HashSet<Guid>? stampedIds = null;
            if (stamped < ids.Count)
            {
                var matched = await outbox.AsNoTracking()
                    .Where(m => ids.Contains(m.Id) && m.LockToken == lockToken)
                    .Select(m => m.Id)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                stampedIds = [.. matched];
            }

            foreach (var message in chunk)
            {
                SyncDelivered(context, message, processedOn, stampedIds?.Contains(message.Id) ?? true);
            }

            pending.RemoveRange(0, chunk.Count);
        }
    }

    /// <summary>
    /// Flushes the buffered delivered rows on an early exit from the batch (a shutdown, or a failed
    /// write), under <see cref="ShutdownStampTimeout"/>. A failure is logged and never thrown, so it
    /// cannot replace the exception already propagating (the polling loop recognizes shutdown by the
    /// <see cref="OperationCanceledException"/> type), and the short budget keeps a dead connection
    /// from holding host shutdown open until the command timeout.
    /// </summary>
    private async Task FlushDeliveredOnExitAsync(ApplicationDbContext context, ClaimedBatch batch)
    {
        if (batch.PendingStamps.Count == 0)
        {
            return;
        }

        using var budget = new CancellationTokenSource(ShutdownStampTimeout, _timeProvider);
        try
        {
            await FlushDeliveredAsync(context, batch, budget.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogExitStampFailed(logger, batch.PendingStamps.Count, ex);
        }
    }

    /// <summary>
    /// Syncs one flushed row with the database. A stamped row carries its <c>ProcessedOn</c> into the
    /// tracker as unchanged and records the processed count, the dispatch lag and the success log
    /// line; a row whose token no longer matched is detached, so nothing later writes it through the
    /// change tracker without the guard.
    /// </summary>
    private void SyncDelivered(ApplicationDbContext context, OutboxMessage message, DateTime processedOn, bool stamped)
    {
        var entry = context.Entry(message);
        if (!stamped)
        {
            LogLeaseLost(logger, message.Id);
            entry.State = EntityState.Detached;
            return;
        }

        message.ProcessedOn = processedOn;
        entry.OriginalValues.SetValues(entry.CurrentValues);
        entry.State = EntityState.Unchanged;

        var eventTypeTag = new KeyValuePair<string, object?>("event_type", message.EventType);
        OutboxMetrics.ProcessedCounter.Add(1, eventTypeTag);

        // End-to-end delivery lag in seconds. Clamped at zero: OccurredOn is stamped by the writing
        // host and ProcessedOn by this one, so clock skew between them must not publish a negative
        // duration into the histogram.
        OutboxMetrics.DispatchLagHistogram.Record(
            Math.Max((processedOn - message.OccurredOn).TotalSeconds, 0),
            eventTypeTag);

        LogMessageProcessed(logger, message.Id, message.EventType);
    }

    /// <summary>
    /// Persists a failed row's retry state (count, last error and the backoff or dead-letter lease
    /// set in memory by the failure handling), guarded by the lock token.
    /// </summary>
    private Task RecordFailureAsync(
        ApplicationDbContext context,
        OutboxMessage message,
        Guid lockToken,
        CancellationToken cancellationToken)
    {
        var retryCount = message.RetryCount;
        var lastError = message.LastError;
        var lockedUntil = message.LockedUntil;

        return StampAsync(
            context,
            message,
            lockToken,
            guarded => guarded.ExecuteUpdateAsync(
                s => s.SetProperty(m => m.RetryCount, retryCount)
                      .SetProperty(m => m.LastError, lastError)
                      .SetProperty(m => m.LockedUntil, lockedUntil),
                cancellationToken));
    }

    /// <summary>
    /// Writes one row's outcome as a set-based update guarded by the lock token: once another
    /// replica has re-claimed the row under a new token (possible only after this replica's lease
    /// expired mid-dispatch), this update matches nothing and drops its stale outcome rather than
    /// overwriting the record of the replica that has since taken the row. An expired lease nobody
    /// has re-claimed still carries this token, so the outcome is stamped. The tracked instance is
    /// then synced (accepted when stamped, detached when not), so nothing later writes the outcome
    /// through the change tracker without the guard.
    /// </summary>
    /// <param name="context">The cycle scope's context holding the row.</param>
    /// <param name="message">The tracked row, already carrying its outcome in memory.</param>
    /// <param name="lockToken">This batch's claim token, which the update is guarded by.</param>
    /// <param name="update">Applies the outcome to the guarded query.</param>
    private async Task StampAsync(
        ApplicationDbContext context,
        OutboxMessage message,
        Guid lockToken,
        Func<IQueryable<OutboxMessage>, Task<int>> update)
    {
        var guarded = context.Set<OutboxMessage>()
            .Where(m => m.Id == message.Id && m.LockToken == lockToken);

        var stamped = await update(guarded).ConfigureAwait(false);

        var entry = context.Entry(message);
        if (stamped == 0)
        {
            LogLeaseLost(logger, message.Id);
            entry.State = EntityState.Detached;
            return;
        }

        entry.OriginalValues.SetValues(entry.CurrentValues);
        entry.State = EntityState.Unchanged;
    }

    /// <summary>
    /// Handles a row whose stored <c>EventType</c> resolved to nothing. The FIRST such attempt is
    /// treated as transient and retried through the normal backoff path: the assembly declaring the
    /// type may simply not be loaded yet (a module assembly resolved lazily, a host still coming up),
    /// and a name that resolves one cycle later was never a dead letter. Only the second attempt is
    /// terminal, which is also the point at which an operator has had a Warning naming the row.
    /// The terminal attempt dead-letters the row the way exhausted retries do (<c>ProcessedOn</c>
    /// stays null and <c>RetryCount</c> is set to <c>MaxRetries</c>), so it leaves the poll but is
    /// listed and replayable by the outbox administration and kept for the dead-letter retention
    /// window, instead of being purged as delivered.
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

        message.RetryCount = _settings.MaxRetries;

        // Stamped, not cleared: the dead-letter retention window runs from LockedUntil (the last
        // failed attempt), and the row already leaves the poll through the RetryCount filter.
        message.LockedUntil = _timeProvider.GetUtcNow().UtcDateTime;
        OutboxMetrics.DeadLetterCounter.Add(
            1,
            new KeyValuePair<string, object?>("event_type", message.EventType),
            new KeyValuePair<string, object?>("reason", "type_unresolvable"));
        LogDeadLetter(logger, message.Id, message.EventType);
        return true;
    }

    /// <summary>
    /// Exponential backoff with jitter for a failed message (see
    /// <see cref="PollingLoop.ComputeRetryBackoffSeconds"/>), capped at the lease so a failing row
    /// never holds its claim longer than a dead replica's rows would.
    /// </summary>
    internal double ComputeRetryBackoffSeconds(int retryCount) =>
        PollingLoop.ComputeRetryBackoffSeconds(retryCount, _settings.RetryBackoffBaseSeconds, _settings.LeaseSeconds);

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
    [LoggerMessage(Level = LogLevel.Warning, Message = "Message bus circuit is open for data source {DataSourceName}: skipping outbox publishes this cycle and retrying the affected messages on a later one")]
    private static partial void LogBrokerCircuitOpen(ILogger logger, string dataSourceName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} was skipped: another replica took over its claim before it was dispatched")]
    private static partial void LogLeaseLostBeforeDispatch(ILogger logger, Guid messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} outlived its claim lease; its outcome was discarded because another replica now owns the row")]
    private static partial void LogLeaseLost(ILogger logger, Guid messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId}: its outcome could not be written during shutdown; a delivered message will be redelivered when its lease expires")]
    private static partial void LogShutdownStampFailed(ILogger logger, Guid messageId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} delivered outbox messages could not be stamped processed as the batch stopped; they will be redelivered when their lease expires")]
    private static partial void LogExitStampFailed(ILogger logger, int count, Exception exception);

    /// <summary>
    /// One claimed batch's progress through <see cref="DispatchMessagesAsync"/>: the claimed rows in
    /// dispatch order, the lock token every renewal and stamp is guarded by, this replica's current
    /// lease expiry, the delivered rows awaiting their batched stamp, and the rows a renewal found
    /// under another token.
    /// </summary>
    private sealed class ClaimedBatch(List<OutboxMessage> messages, Guid lockToken, DateTime leaseUntil)
    {
        public List<OutboxMessage> Messages { get; } = messages;

        public Guid LockToken { get; } = lockToken;

        public DateTime LeaseUntil { get; set; } = leaseUntil;

        public List<OutboxMessage> PendingStamps { get; } = [];

        public HashSet<Guid> LostIds { get; } = [];
    }
}

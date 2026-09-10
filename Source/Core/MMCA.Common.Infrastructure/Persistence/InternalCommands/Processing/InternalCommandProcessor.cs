using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.InternalCommands;
using MMCA.Common.Infrastructure.Context;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.InternalCommands.Administration;
using MMCA.Common.Infrastructure.Persistence.Tenancy;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;

/// <summary>
/// Background service that drains the <c>InternalCommands</c> tables this host owns: it claims due
/// rows with a lease, executes each one through the ordinary CQRS pipeline in a fresh DI scope, and
/// records the outcome.
/// <para>
/// Every relational physical data source in use by this host has its own <c>InternalCommands</c>
/// table and each cycle drains them all, so a service only ever runs the work queued in its own
/// databases. The claim lease is what makes scale-out safe: two replicas racing for a row both issue
/// the same conditional update and exactly one matches.
/// </para>
/// <para>
/// Execution is at-least-once. A replica that dies after its handler committed and before the row
/// was stamped releases the row when its lease expires, and the command runs again. Handlers must be
/// idempotent, which is the same contract the outbox already places on event handlers.
/// </para>
/// </summary>
/// <param name="scopeFactory">Factory for the per-cycle scope and the fresh per-execution scope.</param>
/// <param name="logger">Logger for processing diagnostics.</param>
/// <param name="options">Configurable queue settings.</param>
/// <param name="signal">Signal to wait on between cycles for immediate wake-up.</param>
/// <param name="entityDataSourceRegistry">Registry enumerating the physical data sources in use.</param>
/// <param name="dataSourceResolver">Resolver for the configured scheduling target.</param>
/// <param name="timeProvider">Clock abstraction for the startup delay and every lease, backoff and
/// eligibility timestamp; defaults to <see cref="TimeProvider.System"/> so tests can drive the loop
/// deterministically.</param>
/// <param name="tenancyOptions">Bound tenancy settings, used to discover tenants that keep their own
/// copy of a source: each such database has its own queue table that nothing else would drain.</param>
public sealed partial class InternalCommandProcessor(
    IServiceScopeFactory scopeFactory,
    ILogger<InternalCommandProcessor> logger,
    IOptions<InternalCommandsSettings> options,
    IInternalCommandSignal signal,
    IEntityDataSourceRegistry entityDataSourceRegistry,
    IDataSourceResolver dataSourceResolver,
    TimeProvider? timeProvider = null,
    IOptions<TenancySettings>? tenancyOptions = null) : BackgroundService
{
    /// <summary>
    /// Name of the per-cycle poll activity wrapping the queue fetch. Must stay in sync with
    /// <c>OutboxPollFilterProcessor</c> in MMCA.Common.Aspire, which suppresses these spans and their
    /// SqlClient children from telemetry export (Aspire has no project references, so the string is
    /// deliberately duplicated there).
    /// </summary>
    internal const string PollActivityName = "InternalCommandPoll";

    /// <summary>Column width of <c>LastError</c>; a longer message is truncated to fit.</summary>
    internal const int MaxErrorLength = 4000;

    /// <summary>Floor for the computed wait so an overdue row cannot hot-loop the processor.</summary>
    private static readonly TimeSpan MinimumWait = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Brief startup delay so the host finishes initializing (module registration, migration) before
    /// the first cycle touches the queue table. Matches the outbox processor's delay.
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);

    private static readonly ActivitySource InternalCommandActivitySource = new(InternalCommandMetrics.MeterName);

    private readonly InternalCommandsSettings _settings = options.Value;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(StartupDelay, _timeProvider, stoppingToken).ConfigureAwait(false);

        if (GetTargets().Count == 0)
        {
            LogNoRelationalSources(logger);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            InternalCommandCycleResult cycle = default;
            try
            {
                cycle = await ProcessDueCommandsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogCycleError(logger, ex);
            }

            if (cycle.HasMoreDueWork)
            {
                continue;
            }

            var wait = ComputeWaitTime(
                cycle.EarliestUpcoming,
                _timeProvider.GetUtcNow().UtcDateTime,
                TimeSpan.FromSeconds(_settings.ProcessingDelaySeconds),
                TimeSpan.FromSeconds(_settings.PollingIntervalSeconds));

            await signal.WaitAsync(wait, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Computes how long to wait before the next cycle: until the earliest upcoming row becomes due
    /// (its scheduled instant plus the grace period), capped at the polling interval and floored at
    /// one second so an overdue row another replica holds cannot spin the loop.
    /// </summary>
    /// <param name="earliestUpcoming">The scheduled instant of the oldest not-yet-due row, or null.</param>
    /// <param name="utcNow">The current UTC instant.</param>
    /// <param name="processingDelay">The configured grace period.</param>
    /// <param name="pollingInterval">The configured fallback polling interval.</param>
    /// <returns>The wait before the next cycle.</returns>
    internal static TimeSpan ComputeWaitTime(
        DateTime? earliestUpcoming,
        DateTime utcNow,
        TimeSpan processingDelay,
        TimeSpan pollingInterval)
    {
        if (earliestUpcoming is null)
        {
            return pollingInterval;
        }

        var untilDue = earliestUpcoming.Value + processingDelay - utcNow;
        if (untilDue < MinimumWait)
        {
            untilDue = MinimumWait;
        }

        return untilDue < pollingInterval ? untilDue : pollingInterval;
    }

    /// <summary>
    /// The relational physical sources whose queue tables this host owns: every source backing a
    /// registered entity plus the configured scheduling target (Cosmos has no queue table).
    /// Recomputed per cycle, which is cheap and tolerant of module assemblies loading after startup.
    /// </summary>
    private List<DataSourceKey> GetSources()
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
    /// unit per tenant that keeps its own copy of a source, whose queue table nothing else opens.
    /// </summary>
    internal List<TenantDataSourceTarget> GetTargets() =>
        TenantDataSourceTargets.Expand(GetSources(), tenancyOptions?.Value);

    /// <summary>
    /// Drains every target once and aggregates the per-source results: any source with more due work
    /// triggers an immediate re-poll, the earliest upcoming instant across all sources drives the
    /// smart wait, and the backlog observed across all sources feeds the depth gauge.
    /// </summary>
    /// <param name="cancellationToken">Cancels the cycle.</param>
    /// <returns>The aggregated cycle result.</returns>
    /// <remarks>Internal so tests can drive one cycle without advancing the loop's timers.</remarks>
    internal async Task<InternalCommandCycleResult> ProcessDueCommandsAsync(CancellationToken cancellationToken)
    {
        var hasMoreDueWork = false;
        DateTime? earliestUpcoming = null;
        var pendingDepth = 0L;

        foreach (var target in GetTargets())
        {
            try
            {
                (InternalCommandCycleResult result, long sourceDepth) =
                    await ProcessSourceAsync(target, cancellationToken).ConfigureAwait(false);

                pendingDepth += sourceDepth;
                hasMoreDueWork |= result.HasMoreDueWork;
                if (result.EarliestUpcoming is { } upcoming
                    && (earliestUpcoming is null || upcoming < earliestUpcoming))
                {
                    earliestUpcoming = upcoming;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One unreachable database must not starve the other sources' queues.
                LogSourceError(logger, target.ToString(), ex);
            }
        }

        InternalCommandMetrics.SetPendingDepth(pendingDepth);

        return new InternalCommandCycleResult(hasMoreDueWork, earliestUpcoming);
    }

    /// <summary>
    /// Drains one target and reports both its cycle result and the backlog it observed, so the caller
    /// can sum the depth across sources for the gauge.
    /// </summary>
    private async Task<(InternalCommandCycleResult Cycle, long PendingDepth)> ProcessSourceAsync(
        TenantDataSourceTarget target,
        CancellationToken cancellationToken)
    {
        var sourceName = target.ToString();
        using var scope = scopeFactory.CreateScope();

        // Before the context is asked for, not after: the tenant is what routes the scoped factory to
        // this tenant's database, and it is also what the query filter reads.
        if (target.TenantId is { } tenantId)
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        }

        var context = scope.ServiceProvider.GetRequiredService<IDbContextFactory>().GetDbContext(target.Source);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var cutoff = now.Subtract(TimeSpan.FromSeconds(_settings.ProcessingDelaySeconds));

        var candidates = await FetchCandidatesAsync(context, sourceName, now, cancellationToken).ConfigureAwait(false);
        var pendingDepth = await CountPendingAsync(context, sourceName, candidates.Count, now, cancellationToken)
            .ConfigureAwait(false);

        // The fetch is ordered by ScheduledOn over exactly the pending predicate, so its first row IS
        // the oldest: the gauge costs no extra query, only a subtraction, and a row still in the
        // future reports zero because it is not yet late.
        InternalCommandMetrics.SetOldestDueAge(
            sourceName,
            candidates.Count == 0 ? 0 : Math.Max((now - candidates[0].ScheduledOn).TotalSeconds, 0));

        // Split the ordered batch: the due prefix runs now, the remainder only informs the wait.
        var dueCount = 0;
        while (dueCount < candidates.Count && candidates[dueCount].ScheduledOn <= cutoff)
        {
            dueCount++;
        }

        DateTime? earliestUpcoming = dueCount < candidates.Count ? candidates[dueCount].ScheduledOn : null;

        if (dueCount == 0)
        {
            return (new InternalCommandCycleResult(HasMoreDueWork: false, earliestUpcoming), pendingDepth);
        }

        var claimToken = Guid.NewGuid();
        var claimed = await ClaimDueAsync(context, candidates, dueCount, now, claimToken, cancellationToken)
            .ConfigureAwait(false);

        if (claimed.Count == 0)
        {
            // Another replica claimed the whole prefix between the fetch and the claim.
            return (new InternalCommandCycleResult(HasMoreDueWork: false, earliestUpcoming), pendingDepth);
        }

        LogClaimedBatch(logger, claimed.Count, sourceName);

        var progressed = false;
        foreach (var row in claimed)
        {
            progressed |= await ExecuteClaimedAsync(context, row, sourceName, claimToken, cancellationToken)
                .ConfigureAwait(false);
        }

        return (
            new InternalCommandCycleResult(
                HasMoreDueWork: dueCount == _settings.BatchSize && progressed,
                earliestUpcoming),
            pendingDepth);
    }

    /// <summary>
    /// Fetches the oldest runnable rows for one source. The query runs inside its own activity
    /// (explicit using block) so <c>OutboxPollFilterProcessor</c> can suppress it and its SqlClient
    /// child span from export: an idle fleet polling around the clock would otherwise dominate
    /// telemetry ingestion. No <c>ScheduledOn</c> cutoff in SQL, so the caller can smart-wait until
    /// the earliest upcoming row becomes due; ordering by <c>ScheduledOn</c> guarantees due rows sort
    /// before upcoming ones. Rows under another replica's unexpired lease are skipped entirely.
    /// </summary>
    private async Task<List<InternalCommandMessage>> FetchCandidatesAsync(
        ApplicationDbContext context,
        string sourceName,
        DateTime now,
        CancellationToken cancellationToken)
    {
        using var pollActivity = InternalCommandActivitySource.StartActivity(PollActivityName);
        pollActivity?.SetTag("messaging.internal_commands.data_source", sourceName);

        return await context.Set<InternalCommandMessage>().AsNoTracking()
            .Where(c => c.ProcessedOn == null
                && c.DeadLetteredOn == null
                && c.Attempts < _settings.MaxAttempts
                && (c.ClaimedUntil == null || c.ClaimedUntil < now))
            .OrderBy(c => c.ScheduledOn)
            .ThenBy(c => c.Id)
            .Take(_settings.BatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Backlog depth for one source, derived from the fetch wherever it can be: a batch that came
    /// back short IS the whole backlog, so the steady state costs nothing extra. Only a saturated
    /// batch (exactly the state an operator alerts on) pays for a COUNT, inside the same suppressed
    /// poll activity.
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

        using var pollActivity = InternalCommandActivitySource.StartActivity(PollActivityName);
        pollActivity?.SetTag("messaging.internal_commands.data_source", sourceName);

        return await context.Set<InternalCommandMessage>()
            .Where(c => c.ProcessedOn == null
                && c.DeadLetteredOn == null
                && c.Attempts < _settings.MaxAttempts
                && (c.ClaimedUntil == null || c.ClaimedUntil < now))
            .LongCountAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Claims the due prefix of the fetched batch with a lease before executing anything: a
    /// concurrent replica's claim update wins or loses per row atomically, so two replicas can never
    /// run the same command at the same time. Returns the rows this replica actually owns (empty when
    /// another replica claimed the whole prefix between the fetch and the claim).
    /// </summary>
    private async Task<List<InternalCommandMessage>> ClaimDueAsync(
        ApplicationDbContext context,
        List<InternalCommandMessage> candidates,
        int dueCount,
        DateTime now,
        Guid claimToken,
        CancellationToken cancellationToken)
    {
        Guid[] dueIds = [.. candidates.Take(dueCount).Select(c => c.Id)];
        var leaseUntil = now.AddSeconds(_settings.LeaseSeconds);
        var queue = context.Set<InternalCommandMessage>();

        var claimedCount = await queue
            .Where(c => dueIds.Contains(c.Id)
                && c.ProcessedOn == null
                && c.DeadLetteredOn == null
                && (c.ClaimedUntil == null || c.ClaimedUntil < now))
            .ExecuteUpdateAsync(
                s => s.SetProperty(c => c.ClaimedUntil, leaseUntil).SetProperty(c => c.ClaimedBy, claimToken),
                cancellationToken)
            .ConfigureAwait(false);

        if (claimedCount == 0)
        {
            return [];
        }

        if (claimedCount == dueIds.Length)
        {
            return [.. candidates.Take(dueCount)];
        }

        // Partial claim: run only the rows carrying this replica's token.
        var claimedIds = await queue.AsNoTracking()
            .Where(c => dueIds.Contains(c.Id) && c.ClaimedBy == claimToken)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var claimedSet = claimedIds.ToHashSet();
        return [.. candidates.Take(dueCount).Where(c => claimedSet.Contains(c.Id))];
    }

    /// <summary>
    /// Runs one claimed row and records its outcome. Returns whether the row reached a terminal state
    /// this cycle (completed or dead-lettered), which is what tells the caller a full batch made
    /// progress and is worth re-polling for.
    /// </summary>
    private async Task<bool> ExecuteClaimedAsync(
        ApplicationDbContext context,
        InternalCommandMessage row,
        string sourceName,
        Guid claimToken,
        CancellationToken cancellationToken)
    {
        using var activity = StartExecutionActivity(row, sourceName);

        var command = row.DeserializeCommand();
        if (command is null)
        {
            // Terminal on the first attempt, unlike the outbox: an outbox row's type may simply live
            // in an assembly that has not loaded yet, while a queue row can only be executed by a
            // handler this host registers, and a host that cannot even name the command type has no
            // such handler. Retrying would burn the attempt budget waiting for a fact that will not
            // change.
            await DeadLetterAsync(context, row, claimToken, $"Cannot resolve command type: {row.CommandType}", "type_unresolvable", cancellationToken)
                .ConfigureAwait(false);
            LogTypeUnresolvable(logger, row.Id, row.CommandType);
            return true;
        }

        var startedOn = _timeProvider.GetUtcNow().UtcDateTime;
        var commandTypeTag = new KeyValuePair<string, object?>("command_type", row.CommandType);
        InternalCommandMetrics.LagHistogram.Record(
            Math.Max((startedOn - row.ScheduledOn).TotalSeconds, 0),
            commandTypeTag);

        var startTimestamp = _timeProvider.GetTimestamp();
        (Result? result, Exception? failure) = await InvokeAsync(row, command, cancellationToken).ConfigureAwait(false);
        InternalCommandMetrics.DurationHistogram.Record(
            _timeProvider.GetElapsedTime(startTimestamp).TotalSeconds,
            commandTypeTag);

        if (result is null && failure is null)
        {
            // No handler registered in this host. A deployment fact, not a transient failure, so it
            // is terminal for the same reason an unresolvable type is.
            await DeadLetterAsync(context, row, claimToken, $"No command handler is registered for {row.CommandType}", "handler_missing", cancellationToken)
                .ConfigureAwait(false);
            LogHandlerMissing(logger, row.Id, row.CommandType);
            return true;
        }

        if (failure is null && result is { IsSuccess: true })
        {
            await CompleteAsync(context, row, claimToken, cancellationToken).ConfigureAwait(false);
            InternalCommandMetrics.ProcessedCounter.Add(1, commandTypeTag);
            LogCompleted(logger, row.Id, row.CommandType);
            return true;
        }

        return await RecordFailedAttemptAsync(
            context, row, claimToken, result, failure, commandTypeTag, activity, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Records one failed attempt: either a retry with backoff, or a dead letter when the attempt
    /// budget is spent. A <c>Result.Failure</c> and a thrown exception are the same operational fact
    /// here (the work did not happen), so both consume an attempt and both are recorded verbatim;
    /// the difference an operator cares about is WHY, not which mechanism reported it.
    /// </summary>
    /// <returns><see langword="true"/> when the row was dead-lettered, false when it will retry.</returns>
    private async Task<bool> RecordFailedAttemptAsync(
        ApplicationDbContext context,
        InternalCommandMessage row,
        Guid claimToken,
        Result? result,
        Exception? failure,
        KeyValuePair<string, object?> commandTypeTag,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var reason = failure is null ? "result_failure" : "exception";
        var message = failure?.Message ?? DescribeErrors(result!);
        activity?.SetStatus(ActivityStatusCode.Error, message);
        InternalCommandMetrics.FailedCounter.Add(1, commandTypeTag, new KeyValuePair<string, object?>("reason", reason));

        var attempts = row.Attempts + 1;
        if (attempts >= _settings.MaxAttempts)
        {
            await DeadLetterAsync(context, row, claimToken, message, "attempts_exhausted", cancellationToken)
                .ConfigureAwait(false);
            LogAttemptsExhausted(logger, row.Id, row.CommandType, attempts, message);
            return true;
        }

        await ScheduleRetryAsync(context, row, claimToken, attempts, message, cancellationToken).ConfigureAwait(false);
        LogAttemptFailed(logger, row.Id, row.CommandType, attempts, message);
        return false;
    }

    /// <summary>
    /// Executes one command in a FRESH DI scope, with the scheduling principal and tenant restored,
    /// so it runs exactly as a request would: the same decorators, the same unit of work, the same
    /// authorization answer, the same audit stamps.
    /// </summary>
    /// <returns>
    /// The handler's result and no exception on a completed call; a null result and no exception when
    /// no handler is registered; a null result and the exception when the call threw.
    /// </returns>
    private async Task<(Result? Result, Exception? Failure)> InvokeAsync(
        InternalCommandMessage row,
        IInternalCommand command,
        CancellationToken cancellationToken)
    {
        using var executionScope = scopeFactory.CreateScope();

        // Tenant first: it routes the scoped context factory to the right database, and every
        // repository the handler resolves reads its query filter from it.
        if (row.TenantId is { } tenantId)
        {
            executionScope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        }

        if (BuildPrincipal(row) is { } principal)
        {
            executionScope.ServiceProvider.GetRequiredService<ScopedUserOverride>().Set(principal);
        }

        if (row.CorrelationId is { Length: > 0 } correlationId)
        {
            executionScope.ServiceProvider.GetRequiredService<ICorrelationContext>().SetCorrelationId(correlationId);
        }

        try
        {
            var result = await InternalCommandDispatcher
                .ExecuteAsync(executionScope.ServiceProvider, command, cancellationToken)
                .ConfigureAwait(false);

            return (result, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown, not a command failure. Leave the row leased: it becomes claimable again
            // when the lease expires and the command is retried then, rather than being charged an
            // attempt for work the host never let finish.
            throw;
        }
        catch (Exception ex)
        {
            return (null, ex);
        }
    }

    /// <summary>
    /// Rebuilds the scheduling principal from the row: the user id as the standard <c>sub</c> claim
    /// and each stored role as a role claim, which is exactly what <c>ClaimsPrincipalExtensions</c>
    /// and the authorization decorator read. Returns null for a row scheduled with no user, so that
    /// execution stays anonymous rather than inventing an identity.
    /// </summary>
    private static ClaimsPrincipal? BuildPrincipal(InternalCommandMessage row)
    {
        if (row.UserId is not { } userId)
        {
            return null;
        }

        List<Claim> claims =
        [
            new Claim(AuthClaimTypes.Subject, userId.ToString(CultureInfo.InvariantCulture)),
        ];

        if (row.UserRoles is { Length: > 0 } roles)
        {
            claims.AddRange(roles
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(role => new Claim(ClaimTypes.Role, role)));
        }

        // An authentication type is what makes IsAuthenticated true on the identity; without one the
        // principal reads as anonymous no matter how many claims it carries.
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "InternalCommand"));
    }

    /// <summary>Stamps a completed row, guarded by this replica's claim token.</summary>
    private async Task CompleteAsync(
        ApplicationDbContext context,
        InternalCommandMessage row,
        Guid claimToken,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var attempts = row.Attempts + 1;

        await StampAsync(
            context,
            row.Id,
            claimToken,
            guarded => guarded.ExecuteUpdateAsync(
                s => s.SetProperty(c => c.ProcessedOn, now)
                      .SetProperty(c => c.Attempts, attempts)
                      .SetProperty(c => c.LastError, (string?)null)
                      .SetProperty(c => c.ClaimedUntil, (DateTime?)null)
                      .SetProperty(c => c.ClaimedBy, (Guid?)null),
                cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-leases a failed row for its backoff instead of releasing it. The claim is not cleared
    /// outright because the poll skips leased rows: parking the row under its own lease is what turns
    /// the backoff into an actual wait rather than an immediate re-attempt on the next signal.
    /// </summary>
    private async Task ScheduleRetryAsync(
        ApplicationDbContext context,
        InternalCommandMessage row,
        Guid claimToken,
        int attempts,
        string message,
        CancellationToken cancellationToken)
    {
        var retryAt = _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(ComputeRetryBackoffSeconds(attempts));
        var truncated = Truncate(message);

        await StampAsync(
            context,
            row.Id,
            claimToken,
            guarded => guarded.ExecuteUpdateAsync(
                s => s.SetProperty(c => c.Attempts, attempts)
                      .SetProperty(c => c.LastError, truncated)
                      .SetProperty(c => c.ClaimedUntil, (DateTime?)retryAt),
                cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Abandons a row, recording why, and releases its lease so nothing re-claims it.</summary>
    private async Task DeadLetterAsync(
        ApplicationDbContext context,
        InternalCommandMessage row,
        Guid claimToken,
        string message,
        string reason,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var attempts = row.Attempts + 1;
        var truncated = Truncate(message);

        await StampAsync(
            context,
            row.Id,
            claimToken,
            guarded => guarded.ExecuteUpdateAsync(
                s => s.SetProperty(c => c.Attempts, attempts)
                      .SetProperty(c => c.LastError, truncated)
                      .SetProperty(c => c.DeadLetteredOn, (DateTime?)now)
                      .SetProperty(c => c.ClaimedUntil, (DateTime?)null)
                      .SetProperty(c => c.ClaimedBy, (Guid?)null),
                cancellationToken)).ConfigureAwait(false);

        InternalCommandMetrics.DeadLetterCounter.Add(
            1,
            new KeyValuePair<string, object?>("command_type", row.CommandType),
            new KeyValuePair<string, object?>("reason", reason));
    }

    /// <summary>
    /// Writes one outcome as a set-based update guarded by the claim token: a replica whose lease
    /// expired mid-execution matches nothing here and silently drops its stale outcome rather than
    /// overwriting the record of the replica that has since taken the row.
    /// </summary>
    /// <param name="context">The poll context holding the row.</param>
    /// <param name="id">The row to stamp.</param>
    /// <param name="claimToken">This replica's claim token, which the update is guarded by.</param>
    /// <param name="update">
    /// Applies the outcome to the guarded query. Taken as a delegate over the QUERY rather than as an
    /// expression over EF's setter builder, so the callers below read as ordinary
    /// <c>ExecuteUpdateAsync</c> calls and this method owns only the guard and the lost-lease log.
    /// </param>
    private async Task StampAsync(
        ApplicationDbContext context,
        Guid id,
        Guid claimToken,
        Func<IQueryable<InternalCommandMessage>, Task<int>> update)
    {
        var guarded = context.Set<InternalCommandMessage>()
            .Where(c => c.Id == id && c.ClaimedBy == claimToken);

        var stamped = await update(guarded).ConfigureAwait(false);

        if (stamped == 0)
        {
            LogLeaseLost(logger, id);
        }
    }

    /// <summary>
    /// Exponential backoff for a failed command: <c>base * 2^(attempts - 1)</c>, multiplied by a
    /// random jitter factor in <c>[0.8, 1.2]</c> and then capped at
    /// <c>InternalCommands:MaxRetryBackoffSeconds</c>. The jitter is what keeps a batch that failed
    /// together (one dependency outage fails every row in the same instant) from retrying in lockstep
    /// and re-hammering that dependency on a single shared schedule.
    /// </summary>
    /// <param name="attempts">The attempt number that just failed, counting from one.</param>
    /// <returns>The wait, in seconds, before the row becomes claimable again.</returns>
    internal double ComputeRetryBackoffSeconds(int attempts)
    {
        // Clamp the exponent before it reaches Math.Pow: MaxAttempts is bounded at 20 today, but the
        // cap below is what actually decides the wait, so there is no reason to let a future settings
        // change turn this into an overflow.
        var exponent = Math.Min(Math.Max(attempts - 1, 0), 16);
        var backoff = _settings.RetryBackoffBaseSeconds * Math.Pow(2, exponent);

        // Jitter is applied BEFORE the cap so a capped backoff stays exactly at the ceiling.
#pragma warning disable S2245, CA5394 // Random spaces retry attempts apart (jitter); it feeds no security, token, key or cryptographic decision, so a pseudorandom generator is the correct tool here.
        var jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
#pragma warning restore S2245, CA5394

        return Math.Min(backoff * jitter, _settings.MaxRetryBackoffSeconds);
    }

    /// <summary>
    /// Renders a failed result's errors for the <c>LastError</c> column. Codes and messages only: the
    /// column is read by an operator deciding whether to requeue, and the payload that produced them
    /// is deliberately not projected anywhere (ADR-005).
    /// </summary>
    private static string DescribeErrors(Result result) =>
        result.Errors.Count == 0
            ? "The handler returned a failure with no errors."
            : string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Message}"));

    /// <summary>Truncates a message to the <c>LastError</c> column width, preserving null.</summary>
    private static string? Truncate(string? message) =>
        message is null || message.Length <= MaxErrorLength ? message : message[..MaxErrorLength];

    /// <summary>
    /// Starts an activity linked to the trace context captured at schedule time, so the deferred
    /// execution appears under the request that asked for it. Returns null when no trace context was
    /// captured (a row scheduled outside any activity).
    /// </summary>
    private static Activity? StartExecutionActivity(InternalCommandMessage row, string sourceName)
    {
        if (string.IsNullOrEmpty(row.TraceId) || string.IsNullOrEmpty(row.SpanId))
        {
            return null;
        }

        var parentContext = new ActivityContext(
            ActivityTraceId.CreateFromString(row.TraceId),
            ActivitySpanId.CreateFromString(row.SpanId),
            ActivityTraceFlags.Recorded);

        var activity = InternalCommandActivitySource.StartActivity(
            "InternalCommandExecute",
            ActivityKind.Consumer,
            parentContext);

        activity?.SetTag("messaging.internal_commands.command_id", row.Id.ToString());
        activity?.SetTag("messaging.internal_commands.command_type", row.CommandType);
        activity?.SetTag("messaging.internal_commands.data_source", sourceName);

        return activity;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Internal command processor idle: no relational data sources in use (Cosmos DB does not support the queue table)")]
    private static partial void LogNoRelationalSources(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Internal command cycle failed")]
    private static partial void LogCycleError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Internal command processing failed for data source {DataSourceName}")]
    private static partial void LogSourceError(ILogger logger, string dataSourceName, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Claimed {Count} due internal commands from {DataSourceName}")]
    private static partial void LogClaimedBatch(ILogger logger, int count, string dataSourceName);

    // Debug, not Information: this fires once per executed command and would otherwise be the single
    // noisiest line in steady state, a real telemetry-ingestion cost (rubric section 31, the
    // published COST guide). Failures stay loud.
    [LoggerMessage(Level = LogLevel.Debug, Message = "Internal command {CommandId} ({CommandType}) completed")]
    private static partial void LogCompleted(ILogger logger, Guid commandId, string commandType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Internal command {CommandId} ({CommandType}) failed on attempt {Attempts} and will be retried: {Error}")]
    private static partial void LogAttemptFailed(ILogger logger, Guid commandId, string commandType, int attempts, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Internal command {CommandId} ({CommandType}) dead-lettered after {Attempts} attempts: {Error}")]
    private static partial void LogAttemptsExhausted(ILogger logger, Guid commandId, string commandType, int attempts, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Internal command {CommandId} dead-lettered: command type not resolvable, {CommandType}. Give the command an [InternalCommandName] so its rows carry an identity a rename, namespace move, or assembly move cannot break")]
    private static partial void LogTypeUnresolvable(ILogger logger, Guid commandId, string commandType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Internal command {CommandId} dead-lettered: this host registers no ICommandHandler for {CommandType}. Either the owning module is disabled here, or the row belongs to another service's queue")]
    private static partial void LogHandlerMissing(ILogger logger, Guid commandId, string commandType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Internal command {CommandId} outlived its claim lease; its outcome was discarded because another replica now owns the row")]
    private static partial void LogLeaseLost(ILogger logger, Guid commandId);
}

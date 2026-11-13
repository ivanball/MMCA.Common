using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.InternalCommands.Administration;
using MMCA.Common.Infrastructure.Persistence.Polling;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;

/// <summary>
/// Background service that drains ONE dedicated lane of the <c>InternalCommands</c> tables: the rows of
/// <see cref="InternalCommandLane.CommandType"/>, which the shared <see cref="InternalCommandProcessor"/>
/// excludes while the lane is registered (<see cref="InternalCommandLanes"/>), so a slow or failing
/// receiver never delays the shared queue.
/// <para>
/// It polls with the shared pending predicate, order, batch size, grace period and loop, and claims,
/// renews and runs rows through the shared processor's own row methods, so a lane row executes, retries
/// and dead-letters through the same code as a shared row.
/// </para>
/// <para>
/// Within one target's claimed batch up to <see cref="InternalCommandLane.MaxConcurrency"/> rows run at
/// once, each renewed, run and stamped through its own scope and database context; two rows of one
/// receiver never run at the same time, and among the rows allowed to start the earliest starts first.
/// No row starts once <see cref="InternalCommandLane.BatchStartBudget"/> has elapsed since the batch's
/// first start; when every running row has finished, the claimed rows that never started are released
/// uncharged, so they are due again at once.
/// </para>
/// <para>
/// A receiver whose real attempt failed cools down on this replica for the effective cooldown
/// (<see cref="EffectiveCooldown"/>). While it does, its rows whose turn comes are not sent and their
/// handler is not invoked: each is charged one failed attempt through the unchanged failure path, so it
/// keeps the normal retry timeline and dead-letters on the normal attempt budget. The first row of the
/// receiver whose turn comes at or after the cooldown end runs for real; a success closes the cooldown,
/// a failure reopens it.
/// </para>
/// </summary>
/// <param name="rows">A not-started shared processor whose row methods the lane calls; owned and disposed by the lane.</param>
/// <param name="scopeFactory">Factory for the per-target batch scope and the per-row scopes (the row runner creates the execution scopes).</param>
/// <param name="logger">Logger for lane diagnostics.</param>
/// <param name="signal">The queue's wake-up signal; the framework signal gives the lane a wake of its own.</param>
/// <param name="tableTargets">Decides which databases (and per-tenant copies) hold a queue table this host drains.</param>
/// <param name="timeProvider">Clock for the loop, the cooldowns, the start budget and every eligibility timestamp.</param>
/// <param name="lane">The lane: its name, command type, configured cooldown, receiver key, concurrency and start budget.</param>
internal sealed partial class InternalCommandLaneProcessor(
    InternalCommandProcessor rows,
    IServiceScopeFactory scopeFactory,
    ILogger<InternalCommandLaneProcessor> logger,
    IInternalCommandSignal signal,
    FrameworkTableTargets tableTargets,
    TimeProvider timeProvider,
    InternalCommandLane lane) : BackgroundService
{
    /// <summary>Error code of the failed attempt a cooling receiver's row is charged.</summary>
    internal const string CoolingDownErrorCode = "InternalCommands.ReceiverCoolingDown";

    /// <summary>Error message of the failed attempt a cooling receiver's row is charged.</summary>
    internal const string CoolingDownErrorMessage = "Receiver cooling down after a failed delivery.";

    /// <summary>The jitter floor of <see cref="PollingLoop.ComputeRetryBackoffSeconds"/>.</summary>
    private const double MinimumJitter = 0.8;

    private static readonly ActivitySource LaneActivitySource = new(InternalCommandMetrics.MeterName);

    /// <summary>
    /// The room left for the stamps of the attempt in flight, and the floor of a cooldown that is on.
    /// </summary>
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    private readonly InternalCommandsSettings _settings = rows.Settings;
    private readonly TimeSpan _shortestRetrySpan = ShortestRetrySpan(rows.Settings);
    private readonly TimeSpan _effectiveCooldown = EffectiveCooldown(lane.ReceiverCooldown, lane.BatchStartBudget, rows.Settings);
    private readonly KeyValuePair<string, object?> _laneTag = new("lane", lane.Name);
    private readonly ReceiverCooldowns _cooldowns = new(EffectiveCooldown(lane.ReceiverCooldown, lane.BatchStartBudget, rows.Settings));

    /// <summary>
    /// The wait on the framework signal's lane wake, which is created here, at construction, so a signal
    /// sent between start and the lane's first wait is kept for that wait. Null for any other signal
    /// implementation (the signal is a host singleton the lane does not own).
    /// </summary>
    private readonly Func<TimeSpan, CancellationToken, Task>? _waitForLaneWake = PrepareLaneWake(signal);

    /// <summary>
    /// The cooldown the lane actually applies:
    /// <c>min(configured, S - attemptTimeout - 1 s)</c>, never below 1 second, where
    /// <c>S = sum over k = 1 .. MaxAttempts - 1 of min(0.8 x RetryBackoffBaseSeconds x 2^(k-1), MaxRetryBackoffSeconds)</c>
    /// is the shortest possible span from a row's first attempt to its last. Leaving room for one attempt in
    /// flight is what guarantees, on any number of replicas, that a receiver which answers from the moment a
    /// row is scheduled gets at least one real attempt on that row before it can dead-letter, unless the
    /// cooldown had to be floored at 1 second. When <c>S</c> is zero (<c>MaxAttempts</c> 1) or the configured
    /// value is zero, the cooldown is off and zero is returned.
    /// </summary>
    /// <param name="configured">The configured receiver cooldown.</param>
    /// <param name="attemptTimeout">The longest one attempt can take (the webhook lane passes <c>TimeoutSeconds</c>).</param>
    /// <param name="settings">The internal-command queue settings.</param>
    /// <returns>The effective cooldown.</returns>
    internal static TimeSpan EffectiveCooldown(TimeSpan configured, TimeSpan attemptTimeout, InternalCommandsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var shortestSpan = ShortestRetrySpan(settings);
        if (shortestSpan <= TimeSpan.Zero || configured <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var room = shortestSpan - attemptTimeout - OneSecond;
        var effective = configured < room ? configured : room;
        return effective < OneSecond ? OneSecond : effective;
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        rows.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Drains every target once for this lane's command type and publishes the lane's depth gauge.
    /// Cooldowns that ended more than one cooldown ago are pruned first.
    /// </summary>
    /// <param name="cancellationToken">Cancels the cycle.</param>
    /// <returns>The aggregated cycle result.</returns>
    /// <remarks>Internal so tests can drive one cycle without advancing the loop's timers.</remarks>
    internal async Task<InternalCommandCycleResult> ProcessDueCommandsAsync(CancellationToken cancellationToken)
    {
        _cooldowns.Prune(UtcNow);

        var (hasMoreDueWork, earliestUpcoming, pendingDepth) = await PollingLoop.DrainAllAsync(
            GetTargets(),
            async (target, ct) =>
            {
                (InternalCommandCycleResult result, long sourceDepth) =
                    await ProcessSourceAsync(target, ct).ConfigureAwait(false);
                return (result.HasMoreDueWork, result.EarliestUpcoming, sourceDepth);
            },
            (sourceName, ex) => LogSourceError(logger, lane.Name, sourceName, ex),
            cancellationToken).ConfigureAwait(false);

        InternalCommandMetrics.SetLanePendingDepth(lane.Name, pendingDepth);

        return new InternalCommandCycleResult(hasMoreDueWork, earliestUpcoming);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The start line, the clamp warnings and the custom-signal notice are written before the shared
    /// <see cref="PollingLoop"/> begins its startup delay.
    /// </remarks>
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogLaneStarted(logger, lane.Name, _effectiveCooldown.TotalSeconds);
        if (_effectiveCooldown < lane.ReceiverCooldown)
        {
            LogCooldownClamped(
                logger,
                lane.Name,
                lane.ReceiverCooldown.TotalSeconds,
                lane.BatchStartBudget.TotalSeconds,
                _shortestRetrySpan.TotalSeconds,
                _effectiveCooldown.TotalSeconds);
        }

        if (_shortestRetrySpan > TimeSpan.Zero && _shortestRetrySpan - lane.BatchStartBudget - OneSecond < OneSecond)
        {
            LogRetrySpanTooShort(logger, lane.Name, _shortestRetrySpan.TotalSeconds, lane.BatchStartBudget.TotalSeconds);
        }

        Func<TimeSpan, CancellationToken, Task> waitForSignal;
        if (_waitForLaneWake is { } waitForLaneWake)
        {
            waitForSignal = waitForLaneWake;
        }
        else
        {
            // Waiting on another signal implementation would take the permit the shared loop needs, so
            // the lane falls back to its smart wait and the polling interval.
            LogNoLaneWake(logger, lane.Name);
            waitForSignal = (timeout, ct) => Task.Delay(timeout, timeProvider, ct);
        }

        return PollingLoop.RunAsync(
            timeProvider,
            () => GetTargets().Count > 0,
            () => LogNoRelationalSources(logger, lane.Name),
            async ct =>
            {
                var cycle = await ProcessDueCommandsAsync(ct).ConfigureAwait(false);
                return (cycle.HasMoreDueWork, cycle.EarliestUpcoming);
            },
            ex => LogCycleError(logger, lane.Name, ex),
            _settings.ProcessingDelaySeconds,
            _settings.PollingIntervalSeconds,
            waitForSignal,
            stoppingToken);
    }

    private static Func<TimeSpan, CancellationToken, Task>? PrepareLaneWake(IInternalCommandSignal signal)
    {
        if (signal is not InternalCommandSignal frameworkSignal)
        {
            return null;
        }

        frameworkSignal.EnsureLaneWake();
        return frameworkSignal.WaitForLaneAsync;
    }

    /// <summary>The shortest possible span from a row's first attempt to its last (the jitter floor on every backoff).</summary>
    private static TimeSpan ShortestRetrySpan(InternalCommandsSettings settings)
    {
        var shortestSpanSeconds = 0d;
        for (var attempt = 1; attempt < settings.MaxAttempts; attempt++)
        {
            var exponent = Math.Min(attempt - 1, 16);
            shortestSpanSeconds += Math.Min(
                MinimumJitter * settings.RetryBackoffBaseSeconds * Math.Pow(2, exponent),
                settings.MaxRetryBackoffSeconds);
        }

        return TimeSpan.FromSeconds(shortestSpanSeconds);
    }

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    private IReadOnlyList<TenantDataSourceTarget> GetTargets() =>
        tableTargets.Relational(_settings.DataSource, _settings.DatabaseName);

    /// <summary>
    /// Drains one target for this lane, mirroring the shared loop's per-target step: fetch, count, the
    /// oldest-due gauge, the due split and the batch claim on the batch context; then the claimed batch
    /// runs through <see cref="RunBatchAsync"/>.
    /// </summary>
    private async Task<(InternalCommandCycleResult Cycle, long PendingDepth)> ProcessSourceAsync(
        TenantDataSourceTarget target,
        CancellationToken cancellationToken)
    {
        var sourceName = target.ToString();
        using var scope = scopeFactory.CreateTenantScope(target);

        var context = scope.ServiceProvider.GetRequiredService<IDbContextFactory>().GetDbContext(target.Source);

        var now = UtcNow;
        var cutoff = now.Subtract(TimeSpan.FromSeconds(_settings.ProcessingDelaySeconds));

        var candidates = await FetchCandidatesAsync(context, sourceName, now, cancellationToken).ConfigureAwait(false);
        var pendingDepth = await CountPendingAsync(context, sourceName, candidates.Count, now, cancellationToken)
            .ConfigureAwait(false);

        InternalCommandMetrics.SetLaneOldestDueAge(
            lane.Name,
            sourceName,
            candidates.Count == 0 ? 0 : Math.Max((now - candidates[0].ScheduledOn).TotalSeconds, 0));

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
        var claimed = await rows.ClaimDueAsync(context, candidates, dueCount, now, claimToken, cancellationToken)
            .ConfigureAwait(false);

        if (claimed.Count == 0)
        {
            return (new InternalCommandCycleResult(HasMoreDueWork: false, earliestUpcoming), pendingDepth);
        }

        var batch = new LaneBatch(target, context, sourceName, claimToken);
        var outcome = await RunBatchAsync(batch, claimed, cancellationToken).ConfigureAwait(false);

        // A release happens only after the start budget elapsed, so re-polling for released rows cannot spin.
        var fullBatchMoved = dueCount == _settings.BatchSize && (outcome.Progressed || outcome.ChargedCount > 0);
        var hasMoreDueWork = fullBatchMoved || outcome.ReleasedCount > 0;

        return (new InternalCommandCycleResult(hasMoreDueWork, earliestUpcoming), pendingDepth);
    }

    /// <summary>
    /// Runs one claimed batch: up to <see cref="InternalCommandLane.MaxConcurrency"/> rows at once, never two
    /// of one receiver, the earliest allowed row first. A row whose turn comes while its receiver cools down
    /// is charged instead of started. No turn comes once the start budget has elapsed since the batch's
    /// first start. Every scheduling decision and every cooldown read or write happens here, on one logical
    /// flow; running rows report back only through the task they return.
    /// <para>
    /// The loop always waits for every running row before it returns. A row that throws (other than for
    /// shutdown) stops new turns and is rethrown after the others finish, with no release; shutdown
    /// reaches every running row through the token, stops new turns and writes no release either.
    /// Otherwise the rows that never started are released with one guarded statement on the batch
    /// context, while no row is running.
    /// </para>
    /// </summary>
    private async Task<LaneBatchOutcome> RunBatchAsync(
        LaneBatch batch,
        List<InternalCommandMessage> claimed,
        CancellationToken cancellationToken)
    {
        // Waiting rows in claim order (ScheduledOn, then Id), each with its receiver key read once.
        var state = new LaneBatchState([.. claimed.Select(row => new LaneRow(row, ReadReceiverKey(row)))], lane.MaxConcurrency);

        while (true)
        {
            await StartTurnsAsync(batch, state, cancellationToken).ConfigureAwait(false);
            if (state.Running.Count == 0)
            {
                break;
            }

            await SettleNextAsync(batch, state, cancellationToken).ConfigureAwait(false);
        }

        // Every running row has finished: batch-level statements may use the batch context again.
        var releasedCount = await ReleaseUnstartedAsync(batch, state, cancellationToken).ConfigureAwait(false);

        if (state.ChargedCount > 0 || releasedCount > 0)
        {
            LogBatchSettled(logger, lane.Name, state.ChargedCount, releasedCount, batch.SourceName);
        }

        state.ThrowIfStopped(releasedCount, cancellationToken);

        return new LaneBatchOutcome(state.Progressed, state.ChargedCount, releasedCount);
    }

    /// <summary>
    /// Gives a turn to each waiting row, earliest first, while a slot is free: a row whose receiver has a
    /// row running is passed over; a row whose receiver cools down is charged; any other row starts. Turns
    /// stop for good on shutdown, after a failure, or once the start budget has elapsed since the batch's
    /// first start.
    /// </summary>
    private async Task StartTurnsAsync(LaneBatch batch, LaneBatchState state, CancellationToken cancellationToken)
    {
        var index = 0;
        while (state.StartsOpen && index < state.Waiting.Count && state.Running.Count < lane.MaxConcurrency)
        {
            if (cancellationToken.IsCancellationRequested || IsStartBudgetSpent(state))
            {
                state.StartsOpen = false;
                break;
            }

            var next = state.Waiting[index];
            if (state.IsReceiverBusy(next))
            {
                index++;
                continue;
            }

            // The row's turn: it leaves the waiting list whatever happens next.
            state.Waiting.RemoveAt(index);

            if (next.ReceiverKey is { } key && _cooldowns.IsCoolingDown(key, UtcNow))
            {
                await ChargeTurnAsync(batch, state, next, cancellationToken).ConfigureAwait(false);
                continue;
            }

            state.FirstStart ??= timeProvider.GetTimestamp();
            state.MarkRunning(next, RunRowAsync(batch, next, cancellationToken));
        }
    }

    /// <summary>Whether the start budget has elapsed since the batch's first start (never before a first start).</summary>
    private bool IsStartBudgetSpent(LaneBatchState state) =>
        state.FirstStart is { } firstStart && timeProvider.GetElapsedTime(firstStart) >= lane.BatchStartBudget;

    /// <summary>Charges a row whose turn came while its receiver cools down; a failure of the charge stops new turns.</summary>
    private async Task ChargeTurnAsync(LaneBatch batch, LaneBatchState state, LaneRow row, CancellationToken cancellationToken)
    {
        try
        {
            await ChargeAsync(batch, row.Row, cancellationToken).ConfigureAwait(false);
            state.ChargedCount++;
        }
        catch (Exception ex)
        {
            RecordStop(batch, state, row, ex, cancellationToken);
        }
    }

    /// <summary>
    /// Waits for the next running row to finish and settles it: its progress is counted, its outcome is
    /// applied to its receiver's cooldown, and its receiver is free again. A row that threw stops new turns.
    /// </summary>
    private async Task SettleNextAsync(LaneBatch batch, LaneBatchState state, CancellationToken cancellationToken)
    {
        // Not cancellable on purpose: every running row observes the token itself, and the lane never
        // returns while one is still running.
        var finished = await Task.WhenAny(state.Running.Select(r => r.Execution!)).ConfigureAwait(false);
        var row = state.Running.Find(r => ReferenceEquals(r.Execution, finished))!;

        try
        {
            state.Progressed |= await finished.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordStop(batch, state, row, ex, cancellationToken);
        }
        finally
        {
            ApplyOutcome(row);
            state.MarkFinished(row);
        }
    }

    /// <summary>
    /// Records why a batch stops starting rows: shutdown is kept to be rethrown as such; the first other
    /// exception is kept to be rethrown once every running row has finished; a later one is logged.
    /// </summary>
    private void RecordStop(LaneBatch batch, LaneBatchState state, LaneRow row, Exception exception, CancellationToken cancellationToken)
    {
        state.StartsOpen = false;
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            state.Cancellation ??= ExceptionDispatchInfo.Capture(exception);
        }
        else if (state.Fault is null)
        {
            state.Fault = ExceptionDispatchInfo.Capture(exception);
        }
        else
        {
            LogRowFailedWhileStopping(logger, lane.Name, row.Row.Id, batch.SourceName, exception);
        }
    }

    /// <summary>
    /// Releases the claimed rows that never started, unless the batch stopped for a failure or for
    /// shutdown, and counts them. Called only once no row of the batch is running.
    /// </summary>
    private async Task<int> ReleaseUnstartedAsync(LaneBatch batch, LaneBatchState state, CancellationToken cancellationToken)
    {
        if (state.Waiting.Count == 0 || state.Fault is not null || state.Cancellation is not null
            || cancellationToken.IsCancellationRequested)
        {
            return 0;
        }

        var releasedCount = await ReleaseAsync(batch, state.Waiting, cancellationToken).ConfigureAwait(false);
        if (releasedCount > 0)
        {
            InternalCommandMetrics.LaneReleasedCounter.Add(releasedCount, _laneTag);
        }

        return releasedCount;
    }

    /// <summary>
    /// Runs one row for real through its own tenant scope and database context: renews the claim (a row
    /// another replica has taken over since the batch claim is skipped with a Warning), then executes it
    /// through the shared row runner, which creates the execution scope. A failed attempt is noted on
    /// <paramref name="row"/> for the scheduler, which applies the cooldown once the row has finished.
    /// </summary>
    private async Task<bool> RunRowAsync(LaneBatch batch, LaneRow row, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateTenantScope(batch.Target);
        var context = scope.ServiceProvider.GetRequiredService<IDbContextFactory>().GetDbContext(batch.Target.Source);

        if (!await rows.RenewClaimAsync(context, row.Row, batch.ClaimToken, cancellationToken).ConfigureAwait(false))
        {
            LogLeaseLostBeforeExecution(logger, lane.Name, row.Row.Id);
            return false;
        }

        row.Ran = true;
        return await rows.ExecuteClaimedAsync(
            context,
            row.Row,
            batch.SourceName,
            batch.ClaimToken,
            cancellationToken,
            row.ReceiverKey is null ? null : () => row.MarkFailedAttempt(UtcNow)).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies a finished row's outcome to its receiver's cooldown: a failed real attempt opens (or, for a
    /// trial, reopens) it from the instant the attempt failed, not from now, so the time the scheduler
    /// spent before settling the row (charges of other receivers, the failure write) never pushes the
    /// cooldown's end later than <see cref="EffectiveCooldown"/>'s arithmetic assumes; any other outcome of
    /// a real attempt closes it. A skipped row changes nothing.
    /// </summary>
    private void ApplyOutcome(LaneRow row)
    {
        if (row.ReceiverKey is not { } key || !row.Ran)
        {
            return;
        }

        if (row.FailedAt is { } failedAt)
        {
            OpenCooldown(key, failedAt);
        }
        else if (row.Execution is { IsCompletedSuccessfully: true })
        {
            _cooldowns.Close(key);
        }
    }

    /// <summary>
    /// Charges one row of a cooling receiver a failed attempt without sending it: no renew, no execution
    /// scope, no handler; the unchanged failure path records the attempt, schedules the retry or
    /// dead-letters, and stamps under the batch's claim token on the batch context. The short-circuit
    /// counter is incremented only once that call has returned.
    /// </summary>
    private async Task ChargeAsync(LaneBatch batch, InternalCommandMessage row, CancellationToken cancellationToken)
    {
        using var activity = InternalCommandProcessor.StartExecutionActivity(row, batch.SourceName);

        _ = await rows.RecordFailedAttemptAsync(
            batch.Context,
            row,
            batch.ClaimToken,
            Result.Failure(Error.Failure(CoolingDownErrorCode, CoolingDownErrorMessage)),
            failure: null,
            new KeyValuePair<string, object?>("command_type", row.CommandType),
            activity,
            cancellationToken).ConfigureAwait(false);

        InternalCommandMetrics.LaneShortCircuitedCounter.Add(1, _laneTag);
    }

    /// <summary>
    /// Releases the claimed rows that never started: clears <c>ClaimedUntil</c> and <c>ClaimedBy</c> under the
    /// batch's claim token and changes nothing else, so no attempt is charged and a row another replica has
    /// taken since the batch claim is left untouched. Returns how many rows were released.
    /// </summary>
    private static Task<int> ReleaseAsync(LaneBatch batch, List<LaneRow> unstarted, CancellationToken cancellationToken)
    {
        Guid[] ids = [.. unstarted.Select(r => r.Row.Id)];
        var claimToken = batch.ClaimToken;

        return batch.Context.Set<InternalCommandMessage>()
            .Where(c => ids.Contains(c.Id) && c.ClaimedBy == claimToken)
            .ExecuteUpdateAsync(
                s => s.SetProperty(c => c.ClaimedUntil, (DateTime?)null)
                      .SetProperty(c => c.ClaimedBy, (Guid?)null),
                cancellationToken);
    }

    /// <summary>
    /// Cools a receiver down for one effective cooldown from <paramref name="failedAt"/>, the instant its
    /// real attempt failed, counting and logging a new opening.
    /// </summary>
    private void OpenCooldown(string receiverKey, DateTime failedAt)
    {
        if (_effectiveCooldown <= TimeSpan.Zero)
        {
            return;
        }

        if (_cooldowns.Open(receiverKey, failedAt))
        {
            InternalCommandMetrics.LaneReceiverCooldownsCounter.Add(1, _laneTag);
            LogReceiverCoolingDown(logger, lane.Name, receiverKey, _effectiveCooldown.TotalSeconds);
        }
    }

    /// <summary>
    /// Reads the receiver key of a claimed row. A row that cannot be deserialized, or whose type cannot
    /// be resolved, has no key; the shared row runner dead-letters it exactly as it does today.
    /// </summary>
    private string? ReadReceiverKey(InternalCommandMessage row)
    {
        try
        {
            return row.DeserializeCommand() is { } command ? lane.ReceiverKey(command) : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Fetches the oldest runnable rows of this lane's command type for one source: the shared pending
    /// predicate, order and batch size, inside the same suppressed poll activity as the shared loop.
    /// </summary>
    private async Task<List<InternalCommandMessage>> FetchCandidatesAsync(
        ApplicationDbContext context,
        string sourceName,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var commandType = lane.CommandType;

        using var pollActivity = LaneActivitySource.StartActivity(InternalCommandProcessor.PollActivityName);
        pollActivity?.SetTag("messaging.internal_commands.data_source", sourceName);
        pollActivity?.SetTag("messaging.internal_commands.lane", lane.Name);

        return await context.Set<InternalCommandMessage>().AsNoTracking()
            .Where(c => c.ProcessedOn == null
                && c.DeadLetteredOn == null
                && c.Attempts < _settings.MaxAttempts
                && (c.ClaimedUntil == null || c.ClaimedUntil < now))
            .Where(c => c.CommandType == commandType)
            .OrderBy(c => c.ScheduledOn)
            .ThenBy(c => c.Id)
            .Take(_settings.BatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Lane backlog depth for one source; only a saturated batch pays for a COUNT, as in the shared loop.</summary>
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

        var commandType = lane.CommandType;

        using var pollActivity = LaneActivitySource.StartActivity(InternalCommandProcessor.PollActivityName);
        pollActivity?.SetTag("messaging.internal_commands.data_source", sourceName);
        pollActivity?.SetTag("messaging.internal_commands.lane", lane.Name);

        return await context.Set<InternalCommandMessage>()
            .Where(c => c.ProcessedOn == null
                && c.DeadLetteredOn == null
                && c.Attempts < _settings.MaxAttempts
                && (c.ClaimedUntil == null || c.ClaimedUntil < now))
            .Where(c => c.CommandType == commandType)
            .LongCountAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Internal command lane {Lane} started; a failing receiver cools down for {CooldownSeconds} s")]
    private static partial void LogLaneStarted(ILogger logger, string lane, double cooldownSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Internal command lane {Lane}: the configured receiver cooldown of {ConfiguredSeconds} s does not leave room for one {AttemptTimeoutSeconds} s attempt inside the shortest retry span of {ShortestRetrySpanSeconds} s and was clamped to {EffectiveSeconds} s (0 turns the cooldown off), so a receiver that recovers still gets a real attempt before a row dead-letters")]
    private static partial void LogCooldownClamped(ILogger logger, string lane, double configuredSeconds, double attemptTimeoutSeconds, double shortestRetrySpanSeconds, double effectiveSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Internal command lane {Lane}: the shortest retry span of {ShortestRetrySpanSeconds} s is too short for the recovery guarantee with a {AttemptTimeoutSeconds} s attempt timeout, so the receiver cooldown is floored at 1 s and a receiver that recovers may still see a row dead-letter; raise InternalCommands:MaxAttempts or the retry backoff, or lower Webhooks:TimeoutSeconds")]
    private static partial void LogRetrySpanTooShort(ILogger logger, string lane, double shortestRetrySpanSeconds, double attemptTimeoutSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Internal command lane {Lane} has no wake-up of its own with a custom IInternalCommandSignal; it polls on its smart wait and polling interval instead")]
    private static partial void LogNoLaneWake(ILogger logger, string lane);

    [LoggerMessage(Level = LogLevel.Information, Message = "Internal command lane {Lane} idle: no relational data sources in use (Cosmos DB does not support the queue table)")]
    private static partial void LogNoRelationalSources(ILogger logger, string lane);

    [LoggerMessage(Level = LogLevel.Error, Message = "Internal command lane {Lane} cycle failed")]
    private static partial void LogCycleError(ILogger logger, string lane, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Internal command lane {Lane} processing failed for data source {DataSourceName}")]
    private static partial void LogSourceError(ILogger logger, string lane, string dataSourceName, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Internal command lane {Lane}: command {CommandId} from {DataSourceName} also failed while its batch was stopping after an earlier failure")]
    private static partial void LogRowFailedWhileStopping(ILogger logger, string lane, Guid commandId, string dataSourceName, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Internal command lane {Lane}: receiver {ReceiverKey} failed a delivery attempt and cools down for {CooldownSeconds} s; its due rows meanwhile are not sent and each spends one attempt")]
    private static partial void LogReceiverCoolingDown(ILogger logger, string lane, string receiverKey, double cooldownSeconds);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Internal command lane {Lane} charged {ChargedCount} rows of cooling receivers and released {ReleasedCount} rows that never started, from {DataSourceName}")]
    private static partial void LogBatchSettled(ILogger logger, string lane, int chargedCount, int releasedCount, string dataSourceName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Internal command lane {Lane}: command {CommandId} was skipped, another replica took over its claim before it ran")]
    private static partial void LogLeaseLostBeforeExecution(ILogger logger, string lane, Guid commandId);

    /// <summary>What a batch reports to the cycle result and the Debug line.</summary>
    private readonly record struct LaneBatchOutcome(bool Progressed, int ChargedCount, int ReleasedCount);

    /// <summary>One target's claimed batch: where its rows live and the token every guarded statement carries.</summary>
    /// <param name="Target">The target, from which each running row opens its own tenant scope.</param>
    /// <param name="Context">The batch context: fetch, count, claim, charges and the release.</param>
    /// <param name="SourceName">The target's display name, for logs, metrics and activities.</param>
    /// <param name="ClaimToken">The batch's claim token.</param>
    private sealed record LaneBatch(
        TenantDataSourceTarget Target,
        ApplicationDbContext Context,
        string SourceName,
        Guid ClaimToken);

    /// <summary>
    /// The scheduling state of one claimed batch. Only the batch's scheduling flow reads or writes it; a
    /// running row never touches it.
    /// </summary>
    /// <param name="waiting">The claimed rows, in claim order.</param>
    /// <param name="maxConcurrency">The most rows that run at once.</param>
    private sealed class LaneBatchState(List<LaneRow> waiting, int maxConcurrency)
    {
        private readonly HashSet<string> _busyReceivers = [with(StringComparer.Ordinal)];

        /// <summary>Gets the rows whose turn has not come, in claim order.</summary>
        public List<LaneRow> Waiting { get; } = waiting;

        /// <summary>Gets the rows running now.</summary>
        public List<LaneRow> Running { get; } = [with(maxConcurrency)];

        /// <summary>Gets or sets the timestamp of the batch's first start, if a row has started.</summary>
        public long? FirstStart { get; set; }

        /// <summary>Gets or sets a value indicating whether a real attempt reached a terminal state.</summary>
        public bool Progressed { get; set; }

        /// <summary>Gets or sets how many rows were charged.</summary>
        public int ChargedCount { get; set; }

        /// <summary>Gets or sets a value indicating whether rows may still get a turn.</summary>
        public bool StartsOpen { get; set; } = true;

        /// <summary>Gets or sets the first failure, rethrown once every running row has finished.</summary>
        public ExceptionDispatchInfo? Fault { get; set; }

        /// <summary>Gets or sets the shutdown a running row or a charge observed, rethrown as such.</summary>
        public ExceptionDispatchInfo? Cancellation { get; set; }

        /// <summary>Whether another row of <paramref name="row"/>'s receiver is running.</summary>
        public bool IsReceiverBusy(LaneRow row) => row.ReceiverKey is { } key && _busyReceivers.Contains(key);

        /// <summary>Tracks a started row and holds its receiver.</summary>
        public void MarkRunning(LaneRow row, Task<bool> execution)
        {
            row.Execution = execution;
            Running.Add(row);
            if (row.ReceiverKey is { } key)
            {
                _busyReceivers.Add(key);
            }
        }

        /// <summary>Forgets a finished row and frees its receiver.</summary>
        public void MarkFinished(LaneRow row)
        {
            Running.Remove(row);
            if (row.ReceiverKey is { } key)
            {
                _busyReceivers.Remove(key);
            }
        }

        /// <summary>
        /// Rethrows the failure that stopped the batch, else the shutdown, else a shutdown that stopped the
        /// turns before any running row observed it.
        /// </summary>
        public void ThrowIfStopped(int releasedCount, CancellationToken cancellationToken)
        {
            Fault?.Throw();
            Cancellation?.Throw();
            if (Waiting.Count > 0 && releasedCount == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    /// <summary>
    /// One claimed row of a batch. The scheduler owns it; while the row runs, only its running task writes
    /// <see cref="Ran"/> and <see cref="FailedAt"/>, and the scheduler reads them after that task has
    /// completed.
    /// </summary>
    /// <param name="row">The claimed row.</param>
    /// <param name="receiverKey">The row's receiver key, or null when it has none.</param>
    private sealed class LaneRow(InternalCommandMessage row, string? receiverKey)
    {
        public InternalCommandMessage Row { get; } = row;

        public string? ReceiverKey { get; } = receiverKey;

        /// <summary>Gets or sets the running task, once the row has started; it reports whether the row progressed.</summary>
        public Task<bool>? Execution { get; set; }

        /// <summary>Gets or sets a value indicating whether the renew succeeded and the row was executed for real.</summary>
        public bool Ran { get; set; }

        /// <summary>
        /// Gets the instant the real attempt reached the failure path (read when the shared row runner
        /// reports it, before the failure write), or null when it did not.
        /// </summary>
        public DateTime? FailedAt { get; private set; }

        /// <summary>The shared row runner's failed-attempt observer.</summary>
        /// <param name="failedAt">The instant the attempt failed.</param>
        public void MarkFailedAttempt(DateTime failedAt) => FailedAt = failedAt;
    }
}

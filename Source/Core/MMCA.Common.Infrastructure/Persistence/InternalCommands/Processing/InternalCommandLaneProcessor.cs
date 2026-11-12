using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
/// and dead-letters through the same code as a shared row. Rows run one at a time per replica.
/// </para>
/// <para>
/// A receiver whose real attempt failed cools down on this replica for the effective cooldown
/// (<see cref="EffectiveCooldown"/>). While it does, its due rows are not sent and their handler is not
/// invoked: each is charged one failed attempt through the unchanged failure path, so it keeps the
/// normal retry timeline and dead-letters on the normal attempt budget. The first row of the receiver
/// reached at or after the cooldown end runs for real; a success closes the cooldown, a failure reopens it.
/// </para>
/// </summary>
/// <param name="rows">A not-started shared processor whose row methods the lane calls; owned and disposed by the lane.</param>
/// <param name="scopeFactory">Factory for the per-target scope (the row runner creates the execution scopes).</param>
/// <param name="logger">Logger for lane diagnostics.</param>
/// <param name="options">The internal-command queue settings, shared with the shared loop.</param>
/// <param name="signal">The queue's wake-up signal; the framework signal gives the lane a wake of its own.</param>
/// <param name="tableTargets">Decides which databases (and per-tenant copies) hold a queue table this host drains.</param>
/// <param name="timeProvider">Clock for the loop, the cooldowns and every eligibility timestamp.</param>
/// <param name="lane">The lane: its name, command type, configured cooldown and receiver key.</param>
internal sealed partial class InternalCommandLaneProcessor(
    InternalCommandProcessor rows,
    IServiceScopeFactory scopeFactory,
    ILogger<InternalCommandLaneProcessor> logger,
    IOptions<InternalCommandsSettings> options,
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

    private readonly InternalCommandsSettings _settings = options.Value;
    private readonly TimeSpan _effectiveCooldown = EffectiveCooldown(lane.ReceiverCooldown, options.Value);
    private readonly KeyValuePair<string, object?> _laneTag = new("lane", lane.Name);
    private readonly ReceiverCooldowns _cooldowns = new(EffectiveCooldown(lane.ReceiverCooldown, options.Value));

    /// <summary>
    /// The cooldown the lane actually applies: the configured value, clamped to the shortest possible
    /// span from a row's first attempt to its last,
    /// <c>S = sum over k = 1 .. MaxAttempts - 1 of min(0.8 x RetryBackoffBaseSeconds x 2^(k-1), MaxRetryBackoffSeconds)</c>.
    /// The clamp is what guarantees that a receiver which answers from the moment a row is scheduled
    /// gets at least one real attempt on that row before it can dead-letter. Zero turns the cooldown off.
    /// </summary>
    /// <param name="configured">The configured receiver cooldown.</param>
    /// <param name="settings">The internal-command queue settings.</param>
    /// <returns>The effective cooldown.</returns>
    internal static TimeSpan EffectiveCooldown(TimeSpan configured, InternalCommandsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var shortestSpanSeconds = 0d;
        for (var attempt = 1; attempt < settings.MaxAttempts; attempt++)
        {
            var exponent = Math.Min(attempt - 1, 16);
            shortestSpanSeconds += Math.Min(
                MinimumJitter * settings.RetryBackoffBaseSeconds * Math.Pow(2, exponent),
                settings.MaxRetryBackoffSeconds);
        }

        var shortestSpan = TimeSpan.FromSeconds(shortestSpanSeconds);
        return configured < shortestSpan ? configured : shortestSpan;
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
    /// The start line, the clamp warning and the custom-signal notice are written before the shared
    /// <see cref="PollingLoop"/> begins its startup delay.
    /// </remarks>
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogLaneStarted(logger, lane.Name, _effectiveCooldown.TotalSeconds);
        if (_effectiveCooldown < lane.ReceiverCooldown)
        {
            LogCooldownClamped(logger, lane.Name, lane.ReceiverCooldown.TotalSeconds, _effectiveCooldown.TotalSeconds);
        }

        Func<TimeSpan, CancellationToken, Task> waitForSignal;
        if (signal is InternalCommandSignal frameworkSignal)
        {
            waitForSignal = frameworkSignal.WaitForLaneAsync;
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

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    private IReadOnlyList<TenantDataSourceTarget> GetTargets() =>
        tableTargets.Relational(_settings.DataSource, _settings.DatabaseName);

    /// <summary>
    /// Drains one target for this lane, mirroring the shared loop's per-target step: fetch, count, the
    /// oldest-due gauge, the due split and the batch claim; then one row at a time, either charged (its
    /// receiver is cooling down) or renewed and run through the shared row runner.
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

        (bool progressed, int chargedCount) = await RunBatchAsync(context, claimed, sourceName, claimToken, cancellationToken)
            .ConfigureAwait(false);

        return (
            new InternalCommandCycleResult(
                HasMoreDueWork: dueCount == _settings.BatchSize && (progressed || chargedCount > 0),
                earliestUpcoming),
            pendingDepth);
    }

    /// <summary>
    /// Works through a claimed batch one row at a time, in claim order: a row of a cooling receiver is
    /// charged; any other row is renewed (and skipped with a Warning when another replica took it over)
    /// and then run for real. Returns whether a real attempt reached a terminal state and how many rows
    /// were charged.
    /// </summary>
    private async Task<(bool Progressed, int ChargedCount)> RunBatchAsync(
        ApplicationDbContext context,
        List<InternalCommandMessage> claimed,
        string sourceName,
        Guid claimToken,
        CancellationToken cancellationToken)
    {
        var progressed = false;
        var chargedCount = 0;
        foreach (var row in claimed)
        {
            var receiverKey = ReadReceiverKey(row);

            if (receiverKey is not null && _cooldowns.IsCoolingDown(receiverKey, UtcNow))
            {
                await ChargeAsync(context, row, sourceName, claimToken, cancellationToken).ConfigureAwait(false);
                chargedCount++;
                continue;
            }

            if (!await rows.RenewClaimAsync(context, row, claimToken, cancellationToken).ConfigureAwait(false))
            {
                LogLeaseLostBeforeExecution(logger, lane.Name, row.Id);
                continue;
            }

            progressed |= await RunAsync(context, row, sourceName, claimToken, receiverKey, cancellationToken)
                .ConfigureAwait(false);
        }

        if (chargedCount > 0)
        {
            LogChargedBatch(logger, lane.Name, chargedCount, sourceName);
        }

        return (progressed, chargedCount);
    }

    /// <summary>
    /// Runs one row for real through the shared row runner. A failed attempt opens (or, for a trial,
    /// reopens) the receiver's cooldown; any other outcome closes it.
    /// </summary>
    private async Task<bool> RunAsync(
        ApplicationDbContext context,
        InternalCommandMessage row,
        string sourceName,
        Guid claimToken,
        string? receiverKey,
        CancellationToken cancellationToken)
    {
        if (receiverKey is null)
        {
            return await rows.ExecuteClaimedAsync(context, row, sourceName, claimToken, cancellationToken)
                .ConfigureAwait(false);
        }

        var failed = false;
        var progressed = await rows.ExecuteClaimedAsync(
            context,
            row,
            sourceName,
            claimToken,
            cancellationToken,
            onFailedAttempt: () =>
            {
                failed = true;
                OpenCooldown(receiverKey);
            }).ConfigureAwait(false);

        if (!failed)
        {
            _cooldowns.Close(receiverKey);
        }

        return progressed;
    }

    /// <summary>
    /// Charges one row of a cooling receiver a failed attempt without sending it: no renew, no execution
    /// scope, no handler; the unchanged failure path records the attempt, schedules the retry or
    /// dead-letters, and stamps under the batch's claim token.
    /// </summary>
    private async Task ChargeAsync(
        ApplicationDbContext context,
        InternalCommandMessage row,
        string sourceName,
        Guid claimToken,
        CancellationToken cancellationToken)
    {
        using var activity = InternalCommandProcessor.StartExecutionActivity(row, sourceName);
        InternalCommandMetrics.LaneShortCircuitedCounter.Add(1, _laneTag);

        _ = await rows.RecordFailedAttemptAsync(
            context,
            row,
            claimToken,
            Result.Failure(Error.Failure(CoolingDownErrorCode, CoolingDownErrorMessage)),
            failure: null,
            new KeyValuePair<string, object?>("command_type", row.CommandType),
            activity,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Cools a receiver down after a failed real attempt, counting and logging a new opening.</summary>
    private void OpenCooldown(string receiverKey)
    {
        if (_effectiveCooldown <= TimeSpan.Zero)
        {
            return;
        }

        if (_cooldowns.Open(receiverKey, UtcNow))
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Internal command lane {Lane}: the configured receiver cooldown of {ConfiguredSeconds} s exceeds the shortest retry span of {ShortestRetrySpanSeconds} s and was clamped to it, so a receiver that recovers still gets a real attempt before a row dead-letters")]
    private static partial void LogCooldownClamped(ILogger logger, string lane, double configuredSeconds, double shortestRetrySpanSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Internal command lane {Lane} has no wake-up of its own with a custom IInternalCommandSignal; it polls on its smart wait and polling interval instead")]
    private static partial void LogNoLaneWake(ILogger logger, string lane);

    [LoggerMessage(Level = LogLevel.Information, Message = "Internal command lane {Lane} idle: no relational data sources in use (Cosmos DB does not support the queue table)")]
    private static partial void LogNoRelationalSources(ILogger logger, string lane);

    [LoggerMessage(Level = LogLevel.Error, Message = "Internal command lane {Lane} cycle failed")]
    private static partial void LogCycleError(ILogger logger, string lane, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Internal command lane {Lane} processing failed for data source {DataSourceName}")]
    private static partial void LogSourceError(ILogger logger, string lane, string dataSourceName, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Internal command lane {Lane}: receiver {ReceiverKey} failed a delivery attempt and cools down for {CooldownSeconds} s; its due rows meanwhile are not sent and each spends one attempt")]
    private static partial void LogReceiverCoolingDown(ILogger logger, string lane, string receiverKey, double cooldownSeconds);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Internal command lane {Lane} charged {Count} rows of cooling receivers from {DataSourceName}")]
    private static partial void LogChargedBatch(ILogger logger, string lane, int count, string dataSourceName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Internal command lane {Lane}: command {CommandId} was skipped, another replica took over its claim before it ran")]
    private static partial void LogLeaseLostBeforeExecution(ILogger logger, string lane, Guid commandId);
}

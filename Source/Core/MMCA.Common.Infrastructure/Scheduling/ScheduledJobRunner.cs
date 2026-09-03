using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using CronSchedule = Cronos.CronExpression;

namespace MMCA.Common.Infrastructure.Scheduling;

/// <summary>
/// Background service that runs the host's registered <see cref="IScheduledJob"/>s on their cron
/// schedules, using a persistent job store and the outbox processor's claim-lease idiom so an
/// occurrence executes exactly once across every replica.
/// <para>
/// Each cycle: reconcile the <c>ScheduledJobs</c> rows against the registered jobs, claim due rows
/// one at a time with a lease, execute each claimed job in its own DI scope, stamp the outcome and
/// advance the schedule, then sleep until the earliest upcoming occurrence (capped at
/// <c>Scheduler:PollingIntervalSeconds</c>).
/// </para>
/// <para>
/// Missed occurrences never accumulate: the next run is computed from the instant the execution
/// finished, not from the occurrence that was missed, so a host that was down for a day runs each
/// job once on startup and returns to its cadence rather than replaying the backlog.
/// </para>
/// </summary>
/// <param name="scopeFactory">Factory for the per-cycle scope and the fresh per-execution scope.</param>
/// <param name="logger">Logger for scheduling diagnostics.</param>
/// <param name="schedulerOptions">Configurable scheduler settings.</param>
/// <param name="dataSourceResolver">Resolver for the Default data source holding the job table.</param>
/// <param name="timeProvider">Clock abstraction for the startup delay, the smart wait, and every
/// scheduling timestamp; defaults to <see cref="TimeProvider.System"/> so tests can drive the loop
/// deterministically.</param>
public sealed partial class ScheduledJobRunner(
    IServiceScopeFactory scopeFactory,
    ILogger<ScheduledJobRunner> logger,
    IOptions<SchedulerSettings> schedulerOptions,
    IDataSourceResolver dataSourceResolver,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly SchedulerSettings _settings = schedulerOptions.Value;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>Outcome recorded when a job's <c>ExecuteAsync</c> returned without throwing.</summary>
    internal const string OutcomeSucceeded = "Succeeded";

    /// <summary>Outcome recorded when a job's <c>ExecuteAsync</c> threw.</summary>
    internal const string OutcomeFailed = "Failed";

    /// <summary>
    /// Outcome recorded when the schedule could not be honored at all: the cron expression does not
    /// parse, or the claimed row's job is no longer registered in this host.
    /// </summary>
    internal const string OutcomeSkipped = "Skipped";

    /// <summary>Column width of <c>LastError</c>; longer messages are truncated to fit.</summary>
    internal const int MaxErrorLength = 2048;

    /// <summary>
    /// Brief startup delay so the host finishes initializing (module registration, database
    /// migration) before the first cycle touches the job table. Matches
    /// <c>PeriodicBackgroundService</c>'s delay.
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);

    /// <summary>Floor for the computed wait so an overdue row cannot hot-loop the runner.</summary>
    private static readonly TimeSpan MinimumWait = TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            // Logged once, at startup, and then never again: a disabled scheduler must be visible in
            // the logs of a host that expected it to run, without costing a line per cycle.
            LogSchedulerDisabled(logger);
            return;
        }

        try
        {
            await Task.Delay(StartupDelay, _timeProvider, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            DateTime? earliestNextRun = null;
            try
            {
                earliestNextRun = await RunCycleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown — exit gracefully.
                break;
            }
            catch (Exception ex)
            {
                // One bad cycle (an unreachable database, a model mismatch) must not take the
                // scheduler down for the life of the process: log it and wait out the interval.
                LogCycleError(logger, ex);
            }

            var wait = ComputeWaitTime(
                earliestNextRun,
                _timeProvider.GetUtcNow().UtcDateTime,
                TimeSpan.FromSeconds(_settings.PollingIntervalSeconds));

            try
            {
                await Task.Delay(wait, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Computes how long to sleep before the next cycle: until the earliest upcoming occurrence,
    /// capped at the polling interval and floored at one second so an overdue row that could not be
    /// claimed (another replica holds it) cannot spin the loop.
    /// </summary>
    /// <param name="earliestNextRun">The earliest <c>NextRunOn</c> across the registered jobs, or null when none are registered.</param>
    /// <param name="utcNow">The current UTC instant.</param>
    /// <param name="pollingInterval">The configured fallback polling interval.</param>
    /// <returns>The wait before the next cycle.</returns>
    internal static TimeSpan ComputeWaitTime(DateTime? earliestNextRun, DateTime utcNow, TimeSpan pollingInterval)
    {
        if (earliestNextRun is null)
        {
            return pollingInterval;
        }

        var untilDue = earliestNextRun.Value - utcNow;
        if (untilDue < MinimumWait)
        {
            untilDue = MinimumWait;
        }

        return untilDue < pollingInterval ? untilDue : pollingInterval;
    }

    /// <summary>
    /// Resolves the five-field cron expression in force for a job: the
    /// <c>Scheduler:Jobs:{Name}:Cron</c> override when configured and non-blank, otherwise the
    /// job's compiled-in default.
    /// </summary>
    /// <param name="settings">The bound scheduler settings.</param>
    /// <param name="job">The registered job.</param>
    /// <returns>The expression the schedule should be computed from.</returns>
    internal static string ResolveCronExpression(SchedulerSettings settings, IScheduledJob job) =>
        settings.Jobs.TryGetValue(job.Name, out var overrideSettings)
            && !string.IsNullOrWhiteSpace(overrideSettings.Cron)
                ? overrideSettings.Cron
                : job.CronExpression;

    /// <summary>
    /// Computes the first occurrence strictly after <paramref name="afterUtc"/>, or null when the
    /// expression does not parse or has no further occurrence.
    /// </summary>
    /// <param name="cronExpression">The five-field UTC cron expression.</param>
    /// <param name="afterUtc">The exclusive lower bound.</param>
    /// <param name="error">The parse failure message when the expression is invalid, otherwise null.</param>
    /// <returns>The next occurrence in UTC, or null.</returns>
    internal static DateTime? TryGetNextOccurrence(string cronExpression, DateTime afterUtc, out string? error)
    {
        if (string.IsNullOrWhiteSpace(cronExpression))
        {
            error = "The cron expression is empty.";
            return null;
        }

        try
        {
            error = null;
            return CronSchedule.Parse(cronExpression).GetNextOccurrence(afterUtc, inclusive: false);
        }
        catch (Exception ex) when (ex is Cronos.CronFormatException or ArgumentException)
        {
            // The parser reports a malformed expression as CronFormatException and a structurally
            // impossible argument as ArgumentException. Neither is a reason to take the runner down:
            // both park this one row and leave every other job scheduled.
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Runs one full cycle and reports the earliest upcoming occurrence, which drives the smart wait.
    /// </summary>
    /// <param name="cancellationToken">Cancels the cycle.</param>
    /// <returns>The earliest upcoming occurrence, or null when no jobs are registered.</returns>
    /// <remarks>Internal so tests can drive one cycle without advancing the loop's timers.</remarks>
    internal async Task<DateTime?> RunCycleAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var jobs = ResolveRegisteredJobs(scope.ServiceProvider);
        if (jobs.Count == 0)
        {
            return null;
        }

        var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory>();
        var context = dbContextFactory.GetDbContext(
            dataSourceResolver.ResolveLogical(_settings.DataSource, DataSourceKey.DefaultName));

        await SyncRegistrationsAsync(context, jobs, cancellationToken).ConfigureAwait(false);
        await RunDueJobsAsync(context, jobs, cancellationToken).ConfigureAwait(false);

        string[] names = [.. jobs.Keys];
        return await context.Set<ScheduledJobEntry>().AsNoTracking()
            .Where(e => names.Contains(e.JobName))
            .OrderBy(e => e.NextRunOn)
            .Select(e => (DateTime?)e.NextRunOn)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves this host's registered jobs, keyed by name. A duplicate name is a registration bug
    /// (two jobs would share one schedule row): the first registration wins and the collision is
    /// logged rather than thrown, so one mis-registered module cannot stop every other job.
    /// </summary>
    private Dictionary<string, IScheduledJob> ResolveRegisteredJobs(IServiceProvider serviceProvider)
    {
        var jobs = new Dictionary<string, IScheduledJob>(StringComparer.Ordinal);
        foreach (var group in serviceProvider.GetServices<IScheduledJob>()
            .GroupBy(job => job.Name, StringComparer.Ordinal))
        {
            IScheduledJob[] registered = [.. group];
            jobs[group.Key] = registered[0];
            if (registered.Length > 1)
            {
                LogDuplicateJobName(logger, group.Key);
            }
        }

        return jobs;
    }

    /// <summary>
    /// Reconciles the store against the registered jobs: inserts a row for a job seen for the first
    /// time, and rewrites the expression plus the next occurrence for a job whose schedule changed
    /// in code or configuration. An unparsable expression parks the row at
    /// <see cref="DateTime.MaxValue"/> and records <see cref="OutcomeSkipped"/> instead of throwing:
    /// one bad cron string in configuration must not stop every other job in the host.
    /// </summary>
    private async Task SyncRegistrationsAsync(
        ApplicationDbContext context,
        Dictionary<string, IScheduledJob> jobs,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        string[] names = [.. jobs.Keys];

        var existing = await context.Set<ScheduledJobEntry>().AsNoTracking()
            .Where(e => names.Contains(e.JobName))
            .Select(e => new { e.JobName, e.CronExpression })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var storedCron = existing.ToDictionary(e => e.JobName, e => e.CronExpression, StringComparer.Ordinal);
        var inserted = false;

        foreach (var job in jobs.Values)
        {
            var cron = ResolveCronExpression(_settings, job);
            if (storedCron.TryGetValue(job.Name, out var current)
                && string.Equals(current, cron, StringComparison.Ordinal))
            {
                // Unchanged: leave the row (and its computed NextRunOn) exactly as it is. Recomputing
                // here would push every schedule forward on every cycle and nothing would ever fire.
                continue;
            }

            var nextRun = TryGetNextOccurrence(cron, now, out var parseError);
            if (parseError is not null)
            {
                LogInvalidCronExpression(logger, job.Name, cron, parseError);
            }

            if (storedCron.ContainsKey(job.Name))
            {
                await UpdateScheduleAsync(context, job.Name, cron, nextRun, parseError, cancellationToken)
                    .ConfigureAwait(false);
                LogScheduleChanged(logger, job.Name, current!, cron);
            }
            else
            {
                context.Add(new ScheduledJobEntry
                {
                    JobName = job.Name,
                    CronExpression = cron,
                    NextRunOn = nextRun ?? DateTime.MaxValue,
                    LastOutcome = parseError is null ? null : OutcomeSkipped,
                    LastError = Truncate(parseError),
                });
                inserted = true;
                LogJobRegistered(logger, job.Name, cron);
            }
        }

        if (inserted)
        {
            // Plain SaveChangesAsync without a user id: ScheduledJobEntry is neither auditable nor an
            // aggregate root, so the interceptors have nothing to stamp and no events to capture.
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Rewrites one row's expression and next occurrence through a set-based update, so a row a
    /// replica is currently executing is not disturbed by the change tracker.
    /// </summary>
    private static async Task UpdateScheduleAsync(
        ApplicationDbContext context,
        string jobName,
        string cron,
        DateTime? nextRun,
        string? parseError,
        CancellationToken cancellationToken)
    {
        var effectiveNextRun = nextRun ?? DateTime.MaxValue;

        if (parseError is null)
        {
            await context.Set<ScheduledJobEntry>()
                .Where(e => e.JobName == jobName)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(e => e.CronExpression, cron)
                          .SetProperty(e => e.NextRunOn, effectiveNextRun),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var truncated = Truncate(parseError);
        await context.Set<ScheduledJobEntry>()
            .Where(e => e.JobName == jobName)
            .ExecuteUpdateAsync(
                s => s.SetProperty(e => e.CronExpression, cron)
                      .SetProperty(e => e.NextRunOn, effectiveNextRun)
                      .SetProperty(e => e.LastOutcome, OutcomeSkipped)
                      .SetProperty(e => e.LastError, truncated),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Claims and runs every due job once, one row at a time. Each name is attempted at most once
    /// per cycle, so a row another replica claims between the read and the update simply falls
    /// through to the next cycle instead of spinning this one.
    /// </summary>
    private async Task RunDueJobsAsync(
        ApplicationDbContext context,
        Dictionary<string, IScheduledJob> jobs,
        CancellationToken cancellationToken)
    {
        var attempted = new HashSet<string>(StringComparer.Ordinal);

        while (!cancellationToken.IsCancellationRequested && attempted.Count < jobs.Count)
        {
            string[] candidates = [.. jobs.Keys.Where(name => !attempted.Contains(name))];
            var claim = await TryClaimNextDueAsync(context, candidates, cancellationToken).ConfigureAwait(false);

            if (claim is null)
            {
                return;
            }

            attempted.Add(claim.JobName);

            // A null token means another replica won the race for that row: skip it and look for the
            // next due one rather than spinning on a row this replica will never own.
            if (claim.LockToken is { } lockToken)
            {
                await ExecuteClaimedJobAsync(
                        context, claim.JobName, claim.DueOn, claim.CronExpression, lockToken, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Finds the earliest due, unleased row among <paramref name="candidates"/> and tries to claim it.
    /// </summary>
    /// <returns>
    /// Null when nothing is due; otherwise the row, with a non-null
    /// <see cref="JobClaim.LockToken"/> when this replica won the claim and null when it lost it.
    /// </returns>
    private async Task<JobClaim?> TryClaimNextDueAsync(
        ApplicationDbContext context,
        string[] candidates,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var due = await context.Set<ScheduledJobEntry>().AsNoTracking()
            .Where(e => candidates.Contains(e.JobName)
                && e.NextRunOn <= now
                && (e.LockedUntil == null || e.LockedUntil < now))
            .OrderBy(e => e.NextRunOn)
            .Select(e => new { e.JobName, e.NextRunOn, e.CronExpression })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (due is null)
        {
            return null;
        }

        var lockToken = Guid.NewGuid();
        var leaseUntil = now.AddSeconds(_settings.LeaseSeconds);
        var jobName = due.JobName;

        // The claim is what makes scale-out safe: two replicas racing on the same row both issue this
        // update, and exactly one of them matches the still-unleased predicate.
        var claimed = await context.Set<ScheduledJobEntry>()
            .Where(e => e.JobName == jobName
                && e.NextRunOn <= now
                && (e.LockedUntil == null || e.LockedUntil < now))
            .ExecuteUpdateAsync(
                s => s.SetProperty(e => e.LockedUntil, leaseUntil)
                      .SetProperty(e => e.LockToken, lockToken),
                cancellationToken)
            .ConfigureAwait(false);

        return new JobClaim(due.JobName, due.NextRunOn, due.CronExpression, claimed == 0 ? null : lockToken);
    }

    /// <summary>One due row and the outcome of this replica's attempt to claim it.</summary>
    /// <param name="JobName">The row's job name.</param>
    /// <param name="DueOn">The occurrence the row was due at, used for the lag metric.</param>
    /// <param name="CronExpression">The expression the next occurrence is computed from.</param>
    /// <param name="LockToken">This replica's claim token, or null when another replica won the row.</param>
    private sealed record JobClaim(string JobName, DateTime DueOn, string CronExpression, Guid? LockToken);

    /// <summary>
    /// Executes one claimed job in a fresh DI scope, then stamps the outcome and advances the
    /// schedule. A job exception is caught and recorded: the schedule still advances (so a
    /// permanently failing job cannot hot-loop) and the runner survives.
    /// </summary>
    private async Task ExecuteClaimedJobAsync(
        ApplicationDbContext context,
        string jobName,
        DateTime dueOn,
        string cronExpression,
        Guid lockToken,
        CancellationToken cancellationToken)
    {
        var startedOn = _timeProvider.GetUtcNow().UtcDateTime;

        // Lag is floored at zero: DateTime.MaxValue rows are never claimed, but a clock adjustment
        // between the claim and the start must not publish a negative duration into the histogram.
        var lagSeconds = Math.Max((startedOn - dueOn).TotalSeconds, 0);
        SchedulerMetrics.LagHistogram.Record(lagSeconds, new KeyValuePair<string, object?>("job", jobName));

        var startTimestamp = _timeProvider.GetTimestamp();
        (string outcome, string? error) = await InvokeJobAsync(jobName, cancellationToken).ConfigureAwait(false);
        var elapsed = _timeProvider.GetElapsedTime(startTimestamp);
        var jobTag = new KeyValuePair<string, object?>("job", jobName);
        SchedulerMetrics.DurationHistogram.Record(elapsed.TotalSeconds, jobTag);
        SchedulerMetrics.RunCounter.Add(1, jobTag, new KeyValuePair<string, object?>("outcome", outcome));

        // Missed-run policy: the next occurrence is computed from NOW, not from the occurrence that
        // just ran, so a schedule that elapsed many times while the host was down advances past the
        // present in one step instead of replaying the backlog.
        var completedOn = _timeProvider.GetUtcNow().UtcDateTime;
        var nextRun = TryGetNextOccurrence(cronExpression, completedOn, out var parseError) ?? DateTime.MaxValue;
        if (parseError is not null)
        {
            LogInvalidCronExpression(logger, jobName, cronExpression, parseError);
        }

        var durationMs = (long)elapsed.TotalMilliseconds;
        var truncatedError = Truncate(error);

        // Guarded by the claim token: a replica whose lease expired mid-execution (another replica
        // has since claimed and possibly re-run the row) matches nothing here and silently drops its
        // stale outcome rather than overwriting the current holder's record.
        var stamped = await context.Set<ScheduledJobEntry>()
            .Where(e => e.JobName == jobName && e.LockToken == lockToken)
            .ExecuteUpdateAsync(
                s => s.SetProperty(e => e.LastRunOn, startedOn)
                      .SetProperty(e => e.LastOutcome, outcome)
                      .SetProperty(e => e.LastError, truncatedError)
                      .SetProperty(e => e.LastDurationMs, durationMs)
                      .SetProperty(e => e.NextRunOn, nextRun)
                      .SetProperty(e => e.LockedUntil, (DateTime?)null)
                      .SetProperty(e => e.LockToken, (Guid?)null),
                cancellationToken)
            .ConfigureAwait(false);

        if (stamped == 0)
        {
            LogLeaseLost(logger, jobName);
            return;
        }

        LogJobCompleted(logger, jobName, outcome, durationMs, nextRun);
    }

    /// <summary>
    /// Resolves the job in a fresh DI scope and runs it, returning the outcome and, on failure, the
    /// message to record. Exactly like a request: the job and every scoped dependency it takes (unit
    /// of work, repositories, handlers) are built and disposed around this one run.
    /// </summary>
    private async Task<(string Outcome, string? Error)> InvokeJobAsync(
        string jobName,
        CancellationToken cancellationToken)
    {
        using var jobScope = scopeFactory.CreateScope();
        var job = jobScope.ServiceProvider.GetServices<IScheduledJob>()
            .FirstOrDefault(candidate => string.Equals(candidate.Name, jobName, StringComparison.Ordinal));

        if (job is null)
        {
            LogJobNotResolvable(logger, jobName);
            return (OutcomeSkipped, "The job is no longer registered in this host.");
        }

        try
        {
            LogJobStarting(logger, jobName);
            await job.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return (OutcomeSucceeded, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown, not a job failure. Leave the row leased: it becomes claimable again when
            // the lease expires, and the occurrence is retried then rather than being recorded as a
            // failure it never was.
            throw;
        }
        catch (Exception ex)
        {
            LogJobFailed(logger, jobName, ex);
            return (OutcomeFailed, ex.Message);
        }
    }

    /// <summary>Truncates a message to the <c>LastError</c> column width, preserving null.</summary>
    private static string? Truncate(string? message) =>
        message is null || message.Length <= MaxErrorLength ? message : message[..MaxErrorLength];

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled job runner disabled: set Scheduler:Enabled to true to run recurring jobs")]
    private static partial void LogSchedulerDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduled job cycle failed")]
    private static partial void LogCycleError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled job {JobName} registered with schedule \"{CronExpression}\"")]
    private static partial void LogJobRegistered(ILogger logger, string jobName, string cronExpression);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled job {JobName} schedule changed from \"{PreviousCronExpression}\" to \"{CronExpression}\"; next occurrence recomputed")]
    private static partial void LogScheduleChanged(ILogger logger, string jobName, string previousCronExpression, string cronExpression);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduled job {JobName} has an invalid cron expression \"{CronExpression}\" and will never run: {ParseError}")]
    private static partial void LogInvalidCronExpression(ILogger logger, string jobName, string cronExpression, string parseError);

    [LoggerMessage(Level = LogLevel.Error, Message = "Two scheduled jobs are registered under the name {JobName}; only the first is scheduled")]
    private static partial void LogDuplicateJobName(ILogger logger, string jobName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scheduled job {JobName} has a stored schedule but is no longer registered in this host")]
    private static partial void LogJobNotResolvable(ILogger logger, string jobName);

    // Debug, not Information: this fires once per occurrence and pairs with the completion line
    // below, so a busy schedule would otherwise double this runner's steady-state log volume
    // (rubric §31, the published COST guide). Failures stay loud.
    [LoggerMessage(Level = LogLevel.Debug, Message = "Scheduled job {JobName} starting")]
    private static partial void LogJobStarting(ILogger logger, string jobName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Scheduled job {JobName} finished with outcome {Outcome} in {DurationMs} ms; next occurrence {NextRunOn:O}")]
    private static partial void LogJobCompleted(ILogger logger, string jobName, string outcome, long durationMs, DateTime nextRunOn);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduled job {JobName} failed")]
    private static partial void LogJobFailed(ILogger logger, string jobName, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scheduled job {JobName} outlived its claim lease; its outcome was discarded because another replica now owns the row")]
    private static partial void LogLeaseLost(ILogger logger, string jobName);
}

using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Scheduling;
using Moq;
using static MMCA.Common.Infrastructure.Tests.Scheduling.SchedulerTestHarness;

namespace MMCA.Common.Infrastructure.Tests.Scheduling;

/// <summary>
/// Coverage for <see cref="ScheduledJobRunner"/>: registration sync, the configuration override,
/// the claim lease, outcome recording, and the missed-run policy. Every test drives the runner over
/// a <see cref="FakeTimeProvider"/> and an in-memory SQLite <c>ScheduledJobs</c> table, so the
/// schedule arithmetic is exact rather than timing-dependent.
/// </summary>
public sealed class ScheduledJobRunnerTests
{
    // Hourly on the hour. From the 00:00:00 epoch the first occurrence AFTER now is 01:00:00.
    private const string HourlyCron = "0 * * * *";

    // Every five minutes: the first occurrence after the epoch is 00:05:00.
    private const string FiveMinuteCron = "*/5 * * * *";

    // ── Registration sync: a job seen for the first time gets a row with its first occurrence ──
    [Fact]
    public async Task RunCycleAsync_FirstCycle_InsertsRowWithFirstOccurrenceAfterNow()
    {
        var timeProvider = new FakeTimeProvider(Epoch);
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = SchedulerTestContext.Create(connection);

        var job = new DelegateScheduledJob("nightly", HourlyCron);
        var (runner, scopeServices, _) = CreateRunner(context, EnabledSettings(), timeProvider, job);
        await using var scope = scopeServices;
        using var service = runner;

        await runner.RunCycleAsync(TestContext.Current.CancellationToken);

        var row = await LoadAsync(context, "nightly");
        row.Should().NotBeNull();
        row.CronExpression.Should().Be(HourlyCron);
        row.NextRunOn.Should().Be(EpochUtc.AddHours(1), "the first occurrence strictly after the current instant");
        row.LastRunOn.Should().BeNull();
        row.LastOutcome.Should().BeNull();
        row.LockedUntil.Should().BeNull();
        job.ExecutionCount.Should().Be(0, "nothing is due yet");
    }

    // ── Registration sync: an unchanged schedule is left alone, or nothing would ever fire ──
    [Fact]
    public async Task RunCycleAsync_UnchangedCron_DoesNotPushTheScheduleForward()
    {
        var timeProvider = new FakeTimeProvider(Epoch);
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = SchedulerTestContext.Create(connection);

        var job = new DelegateScheduledJob("nightly", HourlyCron);
        var (runner, scopeServices, _) = CreateRunner(context, EnabledSettings(), timeProvider, job);
        await using var scope = scopeServices;
        using var service = runner;

        await runner.RunCycleAsync(TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromMinutes(30));
        await runner.RunCycleAsync(TestContext.Current.CancellationToken);

        (await LoadAsync(context, "nightly")).NextRunOn.Should().Be(
            EpochUtc.AddHours(1),
            "a cycle that changes nothing must not recompute the next occurrence from the new instant");
    }

    // ── Registration sync: a schedule changed in code rewrites the row and recomputes ──
    [Fact]
    public async Task RunCycleAsync_CronChangedInCode_RewritesExpressionAndRecomputesNextRun()
    {
        var timeProvider = new FakeTimeProvider(Epoch);
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = SchedulerTestContext.Create(connection);

        var settings = EnabledSettings();
        var (firstRunner, firstScope, _) = CreateRunner(
            context, settings, timeProvider, new DelegateScheduledJob("nightly", HourlyCron));
        await using (firstScope)
        using (firstRunner)
        {
            await firstRunner.RunCycleAsync(TestContext.Current.CancellationToken);
        }

        // Same job name, new compiled-in schedule: the redeployed host must adopt it.
        var (runner, scopeServices, _) = CreateRunner(
            context, settings, timeProvider, new DelegateScheduledJob("nightly", FiveMinuteCron));
        await using var scope = scopeServices;
        using var service = runner;

        await runner.RunCycleAsync(TestContext.Current.CancellationToken);

        var row = await LoadAsync(context, "nightly");
        row.CronExpression.Should().Be(FiveMinuteCron);
        row.NextRunOn.Should().Be(EpochUtc.AddMinutes(5));
    }

    // ── Registration sync: the configuration override beats the compiled-in default ──
    [Fact]
    public async Task RunCycleAsync_ConfigurationOverride_WinsOverTheJobDefault()
    {
        var timeProvider = new FakeTimeProvider(Epoch);
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = SchedulerTestContext.Create(connection);

        var settings = EnabledSettings();
        settings.Jobs["nightly"] = new ScheduledJobOverrideSettings { Cron = FiveMinuteCron };

        var (runner, scopeServices, _) = CreateRunner(
            context, settings, timeProvider, new DelegateScheduledJob("nightly", HourlyCron));
        await using var scope = scopeServices;
        using var service = runner;

        await runner.RunCycleAsync(TestContext.Current.CancellationToken);

        var row = await LoadAsync(context, "nightly");
        row.CronExpression.Should().Be(FiveMinuteCron, "the per-job configuration override beats the code default");
        row.NextRunOn.Should().Be(EpochUtc.AddMinutes(5));
    }

    // ── Registration sync: a blank override leaves the code default in force ──
    [Fact]
    public void ResolveCronExpression_BlankOverride_FallsBackToTheJobDefault()
    {
        var settings = EnabledSettings();
        settings.Jobs["nightly"] = new ScheduledJobOverrideSettings { Cron = "   " };

        ScheduledJobRunner.ResolveCronExpression(settings, new DelegateScheduledJob("nightly", HourlyCron))
            .Should().Be(HourlyCron);
    }

    // ── Execution: a due job runs exactly once and its outcome is recorded ──
    [Fact]
    public async Task RunCycleAsync_DueJob_ExecutesOnceAndRecordsSucceeded()
    {
        var timeProvider = new FakeTimeProvider(Epoch);
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = SchedulerTestContext.Create(connection);

        // The job burns two seconds of fake time, so the recorded duration is a real number.
        var job = new DelegateScheduledJob("nightly", HourlyCron, _ =>
        {
            timeProvider.Advance(TimeSpan.FromSeconds(2));
            return Task.CompletedTask;
        });

        var (runner, scopeServices, _) = CreateRunner(context, EnabledSettings(), timeProvider, job);
        await using var scope = scopeServices;
        using var service = runner;

        await runner.RunCycleAsync(TestContext.Current.CancellationToken);   // registers, nothing due
        timeProvider.Advance(TimeSpan.FromHours(1));                          // 01:00:00, the occurrence
        var earliest = await runner.RunCycleAsync(TestContext.Current.CancellationToken);

        job.ExecutionCount.Should().Be(1, "one occurrence is one execution");

        var row = await LoadAsync(context, "nightly");
        row.LastOutcome.Should().Be("Succeeded");
        row.LastError.Should().BeNull();
        row.LastRunOn.Should().Be(EpochUtc.AddHours(1));
        row.LastDurationMs.Should().Be(2000);
        row.NextRunOn.Should().Be(EpochUtc.AddHours(2));
        row.NextRunOn.Should().BeAfter(EpochUtc.AddHours(1).AddSeconds(2), "the schedule must advance strictly past now");
        row.LockedUntil.Should().BeNull("the claim is released once the outcome is stamped");
        row.LockToken.Should().BeNull();
        earliest.Should().Be(EpochUtc.AddHours(2), "the smart wait targets the next occurrence");
    }

    // ── Execution: a failing job is recorded, truncated, and still advanced ──
    [Fact]
    public async Task RunCycleAsync_FailingJob_RecordsFailedWithTruncatedErrorAndStillAdvances()
    {
        var timeProvider = new FakeTimeProvider(Epoch);
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = SchedulerTestContext.Create(connection);

        var longMessage = new string('x', ScheduledJobRunner.MaxErrorLength + 500);
        var job = new DelegateScheduledJob("nightly", HourlyCron, _ => throw new InvalidOperationException(longMessage));

        var (runner, scopeServices, logger) = CreateRunner(context, EnabledSettings(), timeProvider, job);
        await using var scope = scopeServices;
        using var service = runner;

        await runner.RunCycleAsync(TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromHours(1));
        await runner.RunCycleAsync(TestContext.Current.CancellationToken);

        var row = await LoadAsync(context, "nightly");
        row.LastOutcome.Should().Be("Failed");
        row.LastError.Should().NotBeNull();
        row.LastError!.Length.Should().Be(
            ScheduledJobRunner.MaxErrorLength,
            "a long exception message is truncated to the column width rather than failing the update");
        row.NextRunOn.Should().Be(
            EpochUtc.AddHours(2),
            "a failure still advances the schedule, so a permanently failing job cannot hot-loop");
        row.LockedUntil.Should().BeNull();
        VerifyLogged(logger, LogLevel.Error, "LogJobFailed");

        // A second cycle at the same instant must not re-run it: the row is no longer due.
        await runner.RunCycleAsync(TestContext.Current.CancellationToken);
        job.ExecutionCount.Should().Be(1);
    }

    // ── Lease: a row another replica holds is left alone ──
    [Fact]
    public async Task RunCycleAsync_RowUnderAnotherReplicasLease_IsNotClaimed()
    {
        var timeProvider = new FakeTimeProvider(Epoch);
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = SchedulerTestContext.Create(connection);

        var otherReplicaToken = Guid.NewGuid();
        context.Add(new ScheduledJobEntry
        {
            JobName = "nightly",
            CronExpression = HourlyCron,
            NextRunOn = EpochUtc,
            LockedUntil = EpochUtc.AddMinutes(5),
            LockToken = otherReplicaToken,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();

        var job = new DelegateScheduledJob("nightly", HourlyCron);
        var (runner, scopeServices, _) = CreateRunner(context, EnabledSettings(), timeProvider, job);
        await using var scope = scopeServices;
        using var service = runner;

        await runner.RunCycleAsync(TestContext.Current.CancellationToken);

        job.ExecutionCount.Should().Be(0, "an unexpired lease means another replica owns this occurrence");
        var row = await LoadAsync(context, "nightly");
        row.LockToken.Should().Be(otherReplicaToken, "the other replica's claim is untouched");
        row.NextRunOn.Should().Be(EpochUtc, "the occurrence is still owed, just not to this replica");
    }

    // ── Lease: a dead replica's expired claim is reclaimed ──
    [Fact]
    public async Task RunCycleAsync_ExpiredLease_IsReclaimedAndRun()
    {
        var timeProvider = new FakeTimeProvider(Epoch);
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = SchedulerTestContext.Create(connection);

        context.Add(new ScheduledJobEntry
        {
            JobName = "nightly",
            CronExpression = HourlyCron,
            NextRunOn = EpochUtc.AddHours(-1),
            LockedUntil = EpochUtc.AddMinutes(-5),
            LockToken = Guid.NewGuid(),
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();

        var job = new DelegateScheduledJob("nightly", HourlyCron);
        var (runner, scopeServices, _) = CreateRunner(context, EnabledSettings(), timeProvider, job);
        await using var scope = scopeServices;
        using var service = runner;

        await runner.RunCycleAsync(TestContext.Current.CancellationToken);

        job.ExecutionCount.Should().Be(1, "an expired lease is how a dead replica's work is picked up");
        var row = await LoadAsync(context, "nightly");
        row.LastOutcome.Should().Be("Succeeded");
        row.LockedUntil.Should().BeNull();
        row.LockToken.Should().BeNull();
    }

    // ── Missed-run policy: a long outage produces exactly one run, not a catch-up storm ──
    [Fact]
    public async Task RunCycleAsync_ClockJumpedPastManyOccurrences_RunsOnceAndAdvancesPastNow()
    {
        var timeProvider = new FakeTimeProvider(Epoch);
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = SchedulerTestContext.Create(connection);

        var job = new DelegateScheduledJob("nightly", HourlyCron);
        var (runner, scopeServices, _) = CreateRunner(context, EnabledSettings(), timeProvider, job);
        await using var scope = scopeServices;
        using var service = runner;

        await runner.RunCycleAsync(TestContext.Current.CancellationToken);

        // Two full days down: forty-eight hourly occurrences elapsed.
        timeProvider.Advance(TimeSpan.FromDays(2));
        await runner.RunCycleAsync(TestContext.Current.CancellationToken);

        job.ExecutionCount.Should().Be(1, "missed occurrences collapse into one run, never a backlog replay");

        var now = EpochUtc.AddDays(2);
        var row = await LoadAsync(context, "nightly");
        row.NextRunOn.Should().Be(now.AddHours(1), "the next occurrence is computed from now, not from the missed one");
        row.NextRunOn.Should().BeAfter(now);

        // And the very next cycle at the same instant runs nothing.
        await runner.RunCycleAsync(TestContext.Current.CancellationToken);
        job.ExecutionCount.Should().Be(1);
    }

    // ── Invalid cron: recorded as Skipped, parked, and the runner survives ──
    [Fact]
    public async Task RunCycleAsync_InvalidCronExpression_RecordsSkippedAndNeverRunsTheJob()
    {
        var timeProvider = new FakeTimeProvider(Epoch);
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = SchedulerTestContext.Create(connection);

        var broken = new DelegateScheduledJob("broken", "not a cron expression");
        var healthy = new DelegateScheduledJob("healthy", FiveMinuteCron);
        var (runner, scopeServices, logger) = CreateRunner(context, EnabledSettings(), timeProvider, broken, healthy);
        await using var scope = scopeServices;
        using var service = runner;

        await runner.RunCycleAsync(TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromDays(1));
        await runner.RunCycleAsync(TestContext.Current.CancellationToken);

        var brokenRow = await LoadAsync(context, "broken");
        brokenRow.LastOutcome.Should().Be("Skipped");
        brokenRow.LastError.Should().NotBeNullOrWhiteSpace();
        brokenRow.NextRunOn.Should().Be(DateTime.MaxValue, "an unparsable schedule is parked, never claimed");
        broken.ExecutionCount.Should().Be(0);
        VerifyLogged(logger, LogLevel.Error, "LogInvalidCronExpression");

        healthy.ExecutionCount.Should().Be(1, "one bad expression must not stop the other jobs in the host");
    }

    // ── An unregistered row is recorded as Skipped rather than silently held ──
    [Fact]
    public async Task RunCycleAsync_NoRegisteredJobs_DoesNothingAndReportsNoWork()
    {
        var timeProvider = new FakeTimeProvider(Epoch);
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = SchedulerTestContext.Create(connection);

        var (runner, scopeServices, _) = CreateRunner(context, EnabledSettings(), timeProvider);
        await using var scope = scopeServices;
        using var service = runner;

        var earliest = await runner.RunCycleAsync(TestContext.Current.CancellationToken);

        earliest.Should().BeNull();
        (await context.Set<ScheduledJobEntry>().CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    // ── Disabled: the runner logs once and never touches the store ──
    [Fact]
    public async Task ExecuteAsync_Disabled_LogsOnceAndNeverCreatesAScope()
    {
        var scopeFactory = new Mock<IServiceScopeFactory>();
        var logger = new Mock<ILogger<ScheduledJobRunner>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var resolver = new Mock<IDataSourceResolver>();

        using var runner = new ScheduledJobRunner(
            scopeFactory.Object,
            logger.Object,
            Microsoft.Extensions.Options.Options.Create(new SchedulerSettings()),
            resolver.Object,
            new FakeTimeProvider(Epoch));

        await runner.StartAsync(TestContext.Current.CancellationToken);
        await runner.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30), TimeProvider.System);
        await runner.StopAsync(TestContext.Current.CancellationToken);

        runner.ExecuteTask.IsCompletedSuccessfully.Should().BeTrue("the disabled path returns immediately");
        scopeFactory.Verify(f => f.CreateScope(), Times.Never);
        resolver.Verify(r => r.ResolveLogical(It.IsAny<DataSource>(), It.IsAny<string>()), Times.Never);
        VerifyLogged(logger, LogLevel.Information, "LogSchedulerDisabled");
    }

    // ── Smart wait ──
    [Fact]
    public void ComputeWaitTime_NoUpcomingOccurrence_UsesThePollingInterval() =>
        ScheduledJobRunner.ComputeWaitTime(null, EpochUtc, TimeSpan.FromSeconds(30))
            .Should().Be(TimeSpan.FromSeconds(30));

    [Fact]
    public void ComputeWaitTime_OccurrenceSoonerThanTheInterval_SleepsUntilTheOccurrence() =>
        ScheduledJobRunner.ComputeWaitTime(EpochUtc.AddSeconds(8), EpochUtc, TimeSpan.FromSeconds(30))
            .Should().Be(TimeSpan.FromSeconds(8));

    [Fact]
    public void ComputeWaitTime_OccurrenceFurtherOutThanTheInterval_IsCappedAtTheInterval() =>
        ScheduledJobRunner.ComputeWaitTime(EpochUtc.AddHours(4), EpochUtc, TimeSpan.FromSeconds(30))
            .Should().Be(TimeSpan.FromSeconds(30));

    [Fact]
    public void ComputeWaitTime_OverdueOccurrence_IsFlooredSoTheLoopCannotSpin() =>
        ScheduledJobRunner.ComputeWaitTime(EpochUtc.AddMinutes(-5), EpochUtc, TimeSpan.FromSeconds(30))
            .Should().Be(TimeSpan.FromSeconds(1));

    private static async Task<ScheduledJobEntry> LoadAsync(
        MMCA.Common.Infrastructure.Persistence.DbContexts.ApplicationDbContext context,
        string jobName)
    {
        var row = await context.Set<ScheduledJobEntry>().AsNoTracking()
            .SingleOrDefaultAsync(e => e.JobName == jobName, TestContext.Current.CancellationToken);
        row.Should().NotBeNull("a row is expected for the job " + jobName);
        return row;
    }

    // The LoggerMessage source generator names the EventId after the logging method, and its
    // LoggerMessageState does not expose the formatted message via ToString(), so the event id is
    // the stable way to assert THIS specific log entry was written.
    private static void VerifyLogged(Mock<ILogger<ScheduledJobRunner>> logger, LogLevel level, string eventName) =>
        logger.Verify(
            l => l.Log(
                level,
                It.Is<EventId>(e => e.Name == eventName),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
}

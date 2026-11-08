using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Scheduling;
using Moq;
using static MMCA.Common.Infrastructure.Tests.Scheduling.SchedulerTestHarness;

namespace MMCA.Common.Infrastructure.Tests.Scheduling;

/// <summary>
/// Lease renewal for a long-running job: the inverse of the reproduced defect. A job that outlives the
/// claim lease on more than one replica used to run concurrently, lose its completion stamp and never
/// advance its schedule. Each runner gets its own context over one shared-cache in-memory SQLite
/// database, so two replicas can never collide on one connection.
/// </summary>
public sealed class ScheduledJobRunnerLeaseRenewalTests : IAsyncLifetime
{
    private const string FiveMinuteCron = "*/5 * * * *";
    private const string JobName = "long-job";

    private readonly SqliteConnection _root =
        new($"Data Source=renewal-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");

    private readonly FakeTimeProvider _clock = new(Epoch);
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask InitializeAsync() =>
        await _root.OpenAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await _root.DisposeAsync();

    private async Task<SchedulerTestContext> OpenContextAsync()
    {
        var connection = new SqliteConnection(_root.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return SchedulerTestContext.Create(connection);
    }

    private static Task<int> SeedDueRowAsync(ApplicationDbContext context)
    {
        context.Add(new ScheduledJobEntry { JobName = JobName, CronExpression = FiveMinuteCron, NextRunOn = EpochUtc });
        return context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static Task<ScheduledJobEntry> LoadAsync(ApplicationDbContext context) =>
        context.Set<ScheduledJobEntry>().AsNoTracking()
            .SingleAsync(e => e.JobName == JobName, TestContext.Current.CancellationToken);

    private async Task AdvanceAsync(int totalSeconds, ApplicationDbContext observer, DateTime waitUntilLockedPast)
    {
        for (var elapsed = 0; elapsed < totalSeconds; elapsed += 10)
        {
            _clock.Advance(TimeSpan.FromSeconds(10));
            await Task.Yield();
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((await LoadAsync(observer)).LockedUntil is not { } locked || locked <= waitUntilLockedPast)
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the lease should have been renewed past the wait target");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    // A 400 s job against a 300 s lease on two replicas: the holder renews, the second replica does not claim,
    // the job runs exactly once, and its completion stamp lands and advances the schedule.
    [Fact]
    public async Task RunCycleAsync_JobLongerThanTheLease_IsRenewedAndRunsOnceOnTwoReplicas()
    {
        var runs = 0;
        var job = new DelegateScheduledJob(JobName, FiveMinuteCron, async ct =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                _started.TrySetResult();
                await _release.Task.WaitAsync(ct);
            }
        });

        await using var holderContext = await OpenContextAsync();
        await using var otherContext = await OpenContextAsync();
        await using var observer = await OpenContextAsync();
        await SeedDueRowAsync(observer);

        var settings = EnabledSettings(leaseSeconds: 300);
        var (holder, holderScope, _) = CreateRunner(holderContext, settings, _clock, job);
        await using var holderProvider = holderScope;
        using var holderService = holder;
        var (other, otherScope, _) = CreateRunner(otherContext, settings, _clock, job);
        await using var otherProvider = otherScope;
        using var otherService = other;

        var holderRun = Task.Run(() => holder.RunCycleAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        await _started.Task.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, TestContext.Current.CancellationToken);

        // 400 seconds in: the original lease has lapsed, and only a renewal keeps the row unclaimable.
        await AdvanceAsync(400, observer, EpochUtc.AddSeconds(400));

        await other.RunCycleAsync(TestContext.Current.CancellationToken);
        Volatile.Read(ref runs).Should().Be(1, "a job whose lease is renewed must not start on a second replica");

        _release.TrySetResult();
        await holderRun.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, TestContext.Current.CancellationToken);

        var row = await LoadAsync(observer);
        Volatile.Read(ref runs).Should().Be(1);
        row.LastOutcome.Should().Be(ScheduledJobRunner.OutcomeSucceeded);
        row.LastRunOn.Should().Be(EpochUtc, "the run started at the claim");
        row.NextRunOn.Should().Be(EpochUtc.AddMinutes(10), "the next five-minute occurrence after 400 s");
        row.LockedUntil.Should().BeNull();
        row.LockToken.Should().BeNull();

        // Stopped before the stamp: a later clock move must not reach a released row.
        _clock.Advance(TimeSpan.FromSeconds(500));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        (await LoadAsync(observer)).LockedUntil.Should().BeNull();
    }

    // A renewal that matches no row means another replica owns the row: the job is cancelled and nothing is stamped.
    [Fact]
    public async Task RunCycleAsync_WhenTheLeaseIsLost_CancelsTheJobAndStampsNothing()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = new DelegateScheduledJob(JobName, FiveMinuteCron, async ct =>
        {
            _started.TrySetResult();
            try
            {
                await _release.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
        });

        await using var holderContext = await OpenContextAsync();
        await using var observer = await OpenContextAsync();
        await SeedDueRowAsync(observer);

        var (holder, holderScope, logger) = CreateRunner(holderContext, EnabledSettings(leaseSeconds: 300), _clock, job);
        await using var holderProvider = holderScope;
        using var holderService = holder;

        var holderRun = Task.Run(() => holder.RunCycleAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        await _started.Task.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, TestContext.Current.CancellationToken);

        // Another replica takes the row over under its own token.
        var interloper = Guid.NewGuid();
        await observer.Set<ScheduledJobEntry>().Where(e => e.JobName == JobName).ExecuteUpdateAsync(
            s => s.SetProperty(e => e.LockToken, interloper),
            TestContext.Current.CancellationToken);

        for (var elapsed = 0; elapsed < 200 && !cancelled.Task.IsCompleted; elapsed += 10)
        {
            _clock.Advance(TimeSpan.FromSeconds(10));
            await Task.Yield();
        }

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, TestContext.Current.CancellationToken);
        await holderRun.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, TestContext.Current.CancellationToken);

        var row = await LoadAsync(observer);
        row.LockToken.Should().Be(interloper, "the row is the other replica's now");
        row.LastRunOn.Should().BeNull("a run that lost its lease records nothing");
        row.LastOutcome.Should().BeNull();
        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.Is<EventId>(e => e.Name == "LogLeaseLostWhileRunning"),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}

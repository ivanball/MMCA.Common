using System.Globalization;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Services.Api;
using MMCA.Common.UI.Services.Offline;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Offline;

/// <summary>
/// Pins the retry schedule of <see cref="OfflineWriteQueue"/> (rule 15a): while the current user has a
/// write that can be replayed and the device reports online, the queue schedules one replay attempt by
/// itself, 5 seconds after the write is queued or after a pass that made progress, doubling after each
/// pass that made none, capped at 5 minutes. An empty queue, an offline device and a disposed queue
/// hold no timer. Every timer goes through a <see cref="CountingTimeProvider"/> over a
/// <see cref="FakeTimeProvider"/>, so time moves only when a test advances it.
/// </summary>
public sealed class OfflineWriteQueueRetryTests : IDisposable
{
    private const string WidgetKind = "Tests.Widget.Create.v1";
    private const string UnknownKind = "Tests.Gadget.Create.v1";

    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OneTick = TimeSpan.FromTicks(1);

    private readonly InMemoryLocalCacheStore _store = new();
    private readonly ControllableConnectivityService _connectivity = new();
    private readonly SwitchableAuthenticationStateProvider _auth = SwitchableAuthenticationStateProvider.SignedInAs("7");
    private readonly CountingTimeProvider _time = new(new FakeTimeProvider(new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero)));
    private readonly OfflineWriteQueueOptions _options = new();
    private readonly Mock<ILogger<OfflineWriteQueue>> _logger = new();
    private readonly List<OfflineWriteQueue> _queues = [];

    public OfflineWriteQueueRetryTests() =>
        _logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

    public sealed record WidgetPayload(int WidgetId);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Result TransportFailure => Result.Failure(Error.Failure(HttpResultExecutor.TransportErrorCode, "No route."));

    public void Dispose()
    {
        foreach (var queue in _queues)
        {
            queue.Dispose();
        }
    }

    [Fact]
    public void RetryDelays_StartAtFiveSeconds_CapAtFiveMinutes()
    {
        OfflineWriteQueue.InitialRetryDelay.Should().Be(TimeSpan.FromSeconds(5));
        OfflineWriteQueue.MaxRetryDelay.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task EnqueuedOnline_IsReplayedAfterTheFirstDelay_WithoutAConnectivityChange()
    {
        var handler = ScriptedWriteHandler.Returning(WidgetKind, Result.Success());
        var queue = CreateQueue(handler);

        await queue.EnqueueAsync(WidgetKind, new WidgetPayload(1), cancellationToken: Ct);

        _time.DueTimes.Should().Equal(OfflineWriteQueue.InitialRetryDelay);
        _time.Advance(OfflineWriteQueue.InitialRetryDelay - OneTick);
        handler.Calls.Should().BeEmpty("the attempt is not due yet");

        _time.Advance(OneTick);
        await WaitForBackgroundPass(queue);

        handler.Calls.Should().ContainSingle();
        Stored().Should().BeEmpty();
        _time.ActiveTimers.Should().Be(0);
    }

    [Fact]
    public async Task RetryableFailure_DoublesTheDelay_UpToTheCap()
    {
        var handler = ScriptedWriteHandler.Returning(WidgetKind, TransportFailure);
        var queue = CreateQueue(handler);
        await queue.EnqueueAsync(WidgetKind, new WidgetPayload(1), cancellationToken: Ct);
        TimeSpan[] expected =
        [
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40),
            TimeSpan.FromSeconds(80), TimeSpan.FromSeconds(160), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5),
        ];

        for (var pass = 0; pass < expected.Length; pass++)
        {
            _time.DueTimes[^1].Should().Be(expected[pass]);
            _time.ActiveTimers.Should().Be(1, "at most one attempt is scheduled at a time");

            _time.Advance(expected[pass] - OneTick);
            handler.Calls.Should().HaveCount(pass, "the next attempt is not due yet");
            _time.Advance(OneTick);
            await WaitForBackgroundPass(queue);
            handler.Calls.Should().HaveCount(pass + 1);
        }

        _time.DueTimes.Should().HaveCount(expected.Length + 1);
        _time.DueTimes[^1].Should().Be(OfflineWriteQueue.MaxRetryDelay);
        Stored().Should().ContainSingle().Which.Attempts.Should().Be(expected.Length);
    }

    [Fact]
    public async Task PassThatMakesProgress_ResetsTheBackoff()
    {
        var succeedFirst = false;
        var handler = new ScriptedWriteHandler(WidgetKind, (write, _) =>
            Task.FromResult(succeedFirst && write.ReadPayload<WidgetPayload>()!.WidgetId == 1 ? Result.Success() : TransportFailure));
        var queue = CreateQueue(handler);
        await queue.EnqueueAsync(WidgetKind, new WidgetPayload(1), cancellationToken: Ct);
        _time.Advance(TimeSpan.FromSeconds(1));
        await queue.EnqueueAsync(WidgetKind, new WidgetPayload(2), cancellationToken: Ct);

        _time.Advance(TimeSpan.FromSeconds(4));
        await WaitForBackgroundPass(queue);
        _time.DueTimes.Should().Equal(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));

        succeedFirst = true;
        _time.Advance(TimeSpan.FromSeconds(10));
        await WaitForBackgroundPass(queue);

        _time.DueTimes.Should().Equal(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
        Stored().Should().ContainSingle().Which.ReadPayload<WidgetPayload>()!.WidgetId.Should().Be(2);
    }

    [Fact]
    public async Task SuccessfulPass_EmptiesTheQueue_LeavesNoTimer()
    {
        var handler = ScriptedWriteHandler.Returning(WidgetKind, Result.Success());
        var queue = CreateQueue(handler);
        await queue.EnqueueAsync(WidgetKind, new WidgetPayload(1), cancellationToken: Ct);

        _time.Advance(OfflineWriteQueue.InitialRetryDelay);
        await WaitForBackgroundPass(queue);

        Stored().Should().BeEmpty();
        _time.ActiveTimers.Should().Be(0);
        _time.DueTimes.Should().ContainSingle("a drained queue schedules nothing further");
        _time.Advance(TimeSpan.FromHours(1));
        handler.Calls.Should().ContainSingle("an idle queue never wakes up");
    }

    [Fact]
    public async Task GoingOffline_CancelsTheSchedule_ComingBackOnlineReplaysAsBefore()
    {
        var handler = ScriptedWriteHandler.Returning(WidgetKind, Result.Success());
        var queue = CreateQueue(handler);
        await queue.EnqueueAsync(WidgetKind, new WidgetPayload(1), cancellationToken: Ct);
        _time.ActiveTimers.Should().Be(1);

        _connectivity.SetOnline(false);

        _time.ActiveTimers.Should().Be(0, "going offline cancels the scheduled attempt");
        _time.Advance(TimeSpan.FromMinutes(10));
        handler.Calls.Should().BeEmpty();

        _connectivity.SetOnline(true);
        await WaitForBackgroundPass(queue);

        handler.Calls.Should().ContainSingle("the reconnect replays as before");
        Stored().Should().BeEmpty();
        _time.ActiveTimers.Should().Be(0);
    }

    [Fact]
    public async Task EnqueuedOffline_SchedulesNothing()
    {
        _connectivity.SetOnline(false);
        var handler = ScriptedWriteHandler.Returning(WidgetKind, Result.Success());
        var queue = CreateQueue(handler);

        await queue.EnqueueAsync(WidgetKind, new WidgetPayload(1), cancellationToken: Ct);
        _time.Advance(TimeSpan.FromHours(1));

        _time.DueTimes.Should().BeEmpty();
        handler.Calls.Should().BeEmpty();
        Stored().Should().ContainSingle();
    }

    [Fact]
    public async Task EnqueuedWithoutAHandler_SchedulesNothing()
    {
        var queue = CreateQueue(ScriptedWriteHandler.Returning(WidgetKind, Result.Success()));

        await queue.EnqueueAsync(UnknownKind, new WidgetPayload(1), cancellationToken: Ct);

        _time.DueTimes.Should().BeEmpty("a write this app cannot replay gives the schedule nothing to retry");
    }

    [Fact]
    public async Task EmptyQueue_HasNoTimer_AfterConstructionAndAfterAFullDrain()
    {
        Seed(Write("7"), Write("7"));
        var handler = ScriptedWriteHandler.Returning(WidgetKind, Result.Success());
        var queue = CreateQueue(handler);
        _time.DueTimes.Should().BeEmpty("constructing the queue starts no timer");

        (await queue.ReplayAsync(Ct)).Should().Be(2);
        (await queue.ReplayAsync(Ct)).Should().Be(0);

        _time.DueTimes.Should().BeEmpty("a drain that empties the queue schedules nothing");
        _time.ActiveTimers.Should().Be(0);
    }

    [Fact]
    public async Task TwoEnqueuesInARow_LeaveExactlyOneScheduledAttempt()
    {
        var handler = ScriptedWriteHandler.Returning(WidgetKind, Result.Success());
        var queue = CreateQueue(handler);

        await queue.EnqueueAsync(WidgetKind, new WidgetPayload(1), cancellationToken: Ct);
        await queue.EnqueueAsync(WidgetKind, new WidgetPayload(2), cancellationToken: Ct);

        _time.DueTimes.Should().ContainSingle();
        _time.ActiveTimers.Should().Be(1);

        _time.Advance(OfflineWriteQueue.InitialRetryDelay);
        await WaitForBackgroundPass(queue);

        handler.Calls.Should().HaveCount(2, "one pass replays both writes");
        _time.ActiveTimers.Should().Be(0);
    }

    [Fact]
    public async Task Dispose_CancelsTheSchedule_NoPassRunsAfterwards()
    {
        var handler = ScriptedWriteHandler.Returning(WidgetKind, Result.Success());
        var queue = CreateQueue(handler);
        await queue.EnqueueAsync(WidgetKind, new WidgetPayload(1), cancellationToken: Ct);

        queue.Dispose();

        _time.ActiveTimers.Should().Be(0);
        _time.Advance(TimeSpan.FromHours(1));
        await WaitForBackgroundPass(queue);
        handler.Calls.Should().BeEmpty();
        Stored().Should().ContainSingle();
    }

    [Fact]
    public async Task ScheduledAttemptDuringARunningPass_DoesNotStartASecondPass()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = 0;
        var maxInFlight = 0;
        var handler = new ScriptedWriteHandler(WidgetKind, async (_, _) =>
        {
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref maxInFlight, now);
            entered.TrySetResult();
            try
            {
                return await gate.Task;
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
            }
        });
        Seed(Write("7"));
        var queue = CreateQueue(handler);

        var running = queue.ReplayAsync(Ct);
        await entered.Task.WaitAsync(WaitBudget, TimeProvider.System, Ct);
        await queue.EnqueueAsync(WidgetKind, new WidgetPayload(2), cancellationToken: Ct);
        _time.Advance(OfflineWriteQueue.InitialRetryDelay);
        await WaitForBackgroundPass(queue);

        handler.Calls.Should().ContainSingle("the attempt that fired during the running pass is refused by the single-flight guard");

        gate.SetResult(Result.Success());
        (await running.WaitAsync(WaitBudget, TimeProvider.System, Ct)).Should().Be(1);
        _time.ActiveTimers.Should().Be(1, "the write queued during the pass is still pending, so the pass schedules the next attempt");

        _time.Advance(OfflineWriteQueue.InitialRetryDelay);
        await WaitForBackgroundPass(queue);

        handler.Calls.Should().HaveCount(2);
        Volatile.Read(ref maxInFlight).Should().Be(1);
        Stored().Should().BeEmpty();
        _time.ActiveTimers.Should().Be(0);
    }

    [Fact]
    public async Task ScheduledPassFault_IsLogged_AndStillSchedulesTheNextAttempt()
    {
        var queue = CreateQueue(ScriptedWriteHandler.Returning(WidgetKind, Result.Success()));
        await queue.EnqueueAsync(WidgetKind, new WidgetPayload(1), cancellationToken: Ct);
        _store.ThrowOnRead = true;

        _time.Advance(OfflineWriteQueue.InitialRetryDelay);
        await WaitForBackgroundPass(queue);

        _logger.Invocations.Should().Contain(
            i => i.Method.Name == nameof(ILogger.Log) && i.Arguments[3] is InvalidOperationException,
            "the scheduled pass's fault is logged");
        _time.DueTimes.Should().Equal(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
        _time.ActiveTimers.Should().Be(1);
    }

    [Fact]
    public async Task ManualPass_RetryableFailure_SchedulesTheNextAttempt()
    {
        Seed(Write("7"));
        var queue = CreateQueue(ScriptedWriteHandler.Returning(WidgetKind, TransportFailure));

        (await queue.ReplayAsync(Ct)).Should().Be(0);

        _time.DueTimes.Should().Equal(TimeSpan.FromSeconds(10));
        _time.ActiveTimers.Should().Be(1);
    }

    [Fact]
    public async Task ScheduledPass_ReplaysOnlyTheCurrentUsersWrites()
    {
        var foreign = Write("8");
        Seed(foreign);
        var handler = ScriptedWriteHandler.Returning(WidgetKind, Result.Success());
        var queue = CreateQueue(handler);
        var own = (await queue.EnqueueAsync(WidgetKind, new WidgetPayload(1), cancellationToken: Ct)).Value!;

        _time.Advance(OfflineWriteQueue.InitialRetryDelay);
        await WaitForBackgroundPass(queue);

        handler.Calls.Select(w => w.Id).Should().Equal(own.Id);
        Stored().Should().ContainSingle().Which.Id.Should().Be(foreign.Id);
        _time.ActiveTimers.Should().Be(0, "another user's write is nothing this user's queue retries");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (current < value)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }

    private static Task WaitForBackgroundPass(OfflineWriteQueue queue) =>
        queue.BackgroundReplay.WaitAsync(WaitBudget, TimeProvider.System, Ct);

    private PendingWrite Write(string scope) => new()
    {
        Id = Guid.NewGuid(),
        Kind = WidgetKind,
        IdempotencyKey = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
        PayloadJson = "{\"widgetId\":0}",
        UserScope = scope,
        EnqueuedOn = _time.GetUtcNow() - TimeSpan.FromMinutes(1),
    };

    private void Seed(params PendingWrite[] writes) => _store.Seed(_options.StorageKey, writes.ToList());

    private List<PendingWrite> Stored() => _store.Peek<List<PendingWrite>>(_options.StorageKey) ?? [];

    private OfflineWriteQueue CreateQueue(params IOfflineWriteHandler[] handlers)
    {
        var queue = new OfflineWriteQueue(
            _store,
            _connectivity,
            handlers,
            _auth,
            _time,
            Options.Create(_options),
            _logger.Object);
        _queues.Add(queue);
        return queue;
    }
}

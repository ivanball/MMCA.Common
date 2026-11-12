using System.Globalization;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.InternalCommands;
using MMCA.Common.Infrastructure.Persistence.InternalCommands.Administration;
using MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Infrastructure.Tests.Persistence.InternalCommands.Processing;

/// <summary>
/// Tests for Phase 2 of the webhook delivery lane (design-webhook-lane.md 2(b) rules L2.1 to L2.10,
/// 2(c)): bounded concurrency inside one target's claimed batch, same-receiver exclusivity, the cooldown
/// check at a row's turn, the batch start budget and the release of rows that never started, the
/// exception and shutdown paths, and the released counter.
/// <para>
/// Every row renews and stamps through its own scope and context (L2.2), so these tests run on
/// <see cref="InternalCommandLaneDatabase"/>, a temp-file SQLite database with a context per scope.
/// Every handler that must overlap another is HELD on a <see cref="TaskCompletionSource{TResult}"/> and
/// released by the test; every instant is a <see cref="FakeTimeProvider"/> instant that only the test
/// moves. No test sleeps on wall time; the 30-second waits below are safety timeouts on awaited states,
/// never timing assertions.
/// </para>
/// </summary>
public sealed class InternalCommandLaneConcurrencyTests : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(30);
    private static readonly string[] ThreeEarliest = ["a", "b", "c"];
    private static readonly string[] FirstWaveOfThree = ["x1", "y1", "z1"];
    private static readonly string[] FirstWaveOfTwo = ["x1", "y1"];
    private static readonly string[] BothRunningRows = ["a", "b"];

    private readonly FakeTimeProvider _clock = new(InternalCommandTestHarness.Epoch);
    private readonly InternalCommandLaneDatabase _db;
    private InternalCommandsSettings _settings = InternalCommandTestHarness.Settings(maxAttempts: 5);

    public InternalCommandLaneConcurrencyTests() => _db = new InternalCommandLaneDatabase(_clock);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private ConcurrentProbeLog Probes => _db.Probes;

    public void Dispose() => _db.Dispose();

    // L2.1: within one target's claimed batch the lane runs up to DeliveryConcurrency rows at once, never
    // more. Five rows to five receivers, concurrency 3: the three earliest start together; the fourth
    // starts only when a slot frees, and the fifth cannot start while three are held.
    [Fact]
    public async Task Cycle_RunsDifferentReceiversConcurrently_UpToDeliveryConcurrency()
    {
        var lane = CreateLane(maxConcurrency: 3);
        string[] values = ["a", "b", "c", "d", "e"];
        var gates = new Dictionary<string, TaskCompletionSource<Result>>(StringComparer.Ordinal);
        for (var index = 0; index < values.Length; index++)
        {
            _db.Seed($"r-{values[index]}", values[index], Now.AddSeconds(-10 + index));
            gates[values[index]] = Probes.Hold(values[index]);
        }

        var cycle = RunCycle(lane);
        await Within(Probes.WhenStartedCountAsync(3));

        Probes.InFlight.Should().Be(3, "three rows of three different receivers run at once");
        Probes.StartedValues.Should().BeEquivalentTo(ThreeEarliest, "the three earliest rows fill the three slots");

        gates["a"].SetResult(Result.Success());
        await Within(Probes.WhenStartedCountAsync(4));

        Probes.StartedValues.Should().Contain("d").And.NotContain("e", "b, c and d still hold every slot");

        Probes.ReleaseAll();
        await Within(cycle);

        Probes.MaxInFlight.Should().Be(3, "never more than DeliveryConcurrency rows at once");
        foreach (var value in values)
        {
            Probes.ValuesFor($"r-{value}").Should().Equal(value);
        }
    }

    // L2.2: each running row renews and stamps through its own tenant scope and context; the batch
    // context runs only the batch statements (here the fetch and the claim). Three rows run at the same
    // time (all three are held until all three have started), so a shared context could not serve them.
    [Fact]
    public async Task Cycle_EachRunningRowStampsThroughItsOwnContext()
    {
        var lane = CreateLane(maxConcurrency: 3);
        var rows = new[]
        {
            _db.Seed("r1", "a", Now.AddSeconds(-3)),
            _db.Seed("r2", "b", Now.AddSeconds(-2)),
            _db.Seed("r3", "c", Now.AddSeconds(-1)),
        };
        foreach (var value in new[] { "a", "b", "c" })
        {
            Probes.Hold(value);
        }

        var cycle = RunCycle(lane);
        await Within(Probes.WhenStartedCountAsync(3));
        Probes.InFlight.Should().Be(3, "the three rows overlap");
        Probes.ReleaseAll();
        await Within(cycle);

        var rowIds = rows.Select(r => r.Id).ToHashSet();
        var laneCommands = _db.Commands.All.Where(c => c.Context > 0).ToList();
        var batchContext = laneCommands.Where(c => c.IsFetch).Select(c => c.Context).Distinct()
            .Should().ContainSingle("one batch context fetches the batch").Subject;

        laneCommands.Where(c => c.Context == batchContext && c.IsUpdate).Should().ContainSingle(
            "the batch context runs the claim and no row's renew or stamp");

        var perRowContext = laneCommands
            .Where(c => c.Context != batchContext && c.IsUpdate)
            .GroupBy(c => c.Context)
            .ToList();
        perRowContext.Should().HaveCount(rows.Length, "one context per running row");
        perRowContext.Should().AllSatisfy(group =>
        {
            group.SelectMany(c => c.Ids).Where(rowIds.Contains).Distinct()
                .Should().ContainSingle("a row's context renews and stamps that row only");
            group.Should().HaveCount(2, "its renew and its completion stamp");
        });
        perRowContext.Select(g => g.SelectMany(c => c.Ids).Where(rowIds.Contains).Distinct().Single())
            .Should().BeEquivalentTo(rowIds, "every row was renewed and stamped on its own context");

        foreach (var row in rows)
        {
            (await _db.ReloadAsync(row.Id, Ct)).ProcessedOn.Should().Be(Now);
        }
    }

    // L2.3: two rows of one receiver never run at the same time on one replica; among the rows allowed to
    // start, the earliest (ScheduledOn, then Id) starts first. Concurrency 3 over x1 x2 y1 x3 z1 w1 (in
    // that order): the first wave is x1, y1, z1 (x2 and x3 wait for x1; w1 is later than z1). Freeing x1
    // starts x2, the earliest allowed row, not w1. Freeing y1 then starts w1, because x3, though earlier,
    // still waits for x2. Freeing x2 starts x3.
    [Fact]
    public async Task Cycle_NeverOverlapsOneReceiver_AndStartsInScheduledOrder()
    {
        var lane = CreateLane(maxConcurrency: 3);
        (string Receiver, string Value)[] plan =
            [("x", "x1"), ("x", "x2"), ("y", "y1"), ("x", "x3"), ("z", "z1"), ("w", "w1")];
        var gates = new Dictionary<string, TaskCompletionSource<Result>>(StringComparer.Ordinal);
        for (var index = 0; index < plan.Length; index++)
        {
            _db.Seed(plan[index].Receiver, plan[index].Value, Now.AddSeconds(-10 + index));
            gates[plan[index].Value] = Probes.Hold(plan[index].Value);
        }

        var cycle = RunCycle(lane);

        await Within(Probes.WhenStartedCountAsync(3));
        Probes.StartedValues.Should().BeEquivalentTo(FirstWaveOfThree, "x2 and x3 wait for x1; w1 is the latest row");

        gates["x1"].SetResult(Result.Success());
        await Within(Probes.WhenStartedCountAsync(4));
        Probes.StartedValues[3].Should().Be("x2", "x2 is the earliest row allowed to start once x1 is done");

        gates["y1"].SetResult(Result.Success());
        await Within(Probes.WhenStartedCountAsync(5));
        Probes.StartedValues[4].Should().Be("w1", "x3 is earlier but its receiver is still busy with x2");

        gates["x2"].SetResult(Result.Success());
        await Within(Probes.WhenStartedCountAsync(6));
        Probes.StartedValues[5].Should().Be("x3");

        Probes.ReleaseAll();
        await Within(cycle);

        Probes.MaxInFlightPerReceiver.Should().Be(1, "one receiver never has two rows in flight on one replica");
        Probes.ValuesFor("x").Should().Equal("x1", "x2", "x3");
    }

    // L2.4: the cooldown check is made when a row's turn comes. x2 waits for x1 (same receiver); x1's real
    // attempt fails and cools x down; x2's turn then comes and it is charged, never sent.
    [Fact]
    public async Task Cycle_ARowWhoseReceiverCooledDownWhileItWaited_IsChargedWhenItsTurnComes()
    {
        var lane = CreateLane(maxConcurrency: 2);
        _db.Seed("x", "x1", Now.AddSeconds(-3));
        var x2 = _db.Seed("x", "x2", Now.AddSeconds(-2));
        var y1 = _db.Seed("y", "y1", Now.AddSeconds(-1));
        var x1Gate = Probes.Hold("x1");
        var y1Gate = Probes.Hold("y1");

        var cycle = RunCycle(lane);
        await Within(Probes.WhenStartedCountAsync(2));
        Probes.StartedValues.Should().BeEquivalentTo(FirstWaveOfTwo, "x2 waits for its receiver");

        x1Gate.SetResult(ConcurrentProbeLog.FailedDelivery());
        await Within(Probes.WhenFinishedAsync("x1"));
        y1Gate.SetResult(Result.Success());
        await Within(cycle);

        Probes.ValuesFor("x").Should().Equal(["x1"], "x was cooling by the time x2's turn came");
        var storedX2 = await _db.ReloadAsync(x2.Id, Ct);
        storedX2.Attempts.Should().Be(1);
        storedX2.LastError.Should().Be(InternalCommandLaneDatabase.CoolingDownError);
        (await _db.ReloadAsync(y1.Id, Ct)).ProcessedOn.Should().NotBeNull();
    }

    // L2.5: once TimeoutSeconds (the batch start budget) have elapsed since the batch's first start, no new
    // row starts; when every running row has finished, each claimed row that never started is RELEASED
    // (claim cleared, nothing else changed, no attempt charged). Concurrency 2, budget 10 s: a and b start
    // at T0; at T0+5 a frees its slot and c starts (5 < 10); at T0+11 b and c finish and d does not start.
    [Fact]
    public async Task Cycle_BudgetElapsed_StopsNewStarts_AndReleasesTheRestUncharged()
    {
        var lane = CreateLane(maxConcurrency: 2, budget: Budget);
        _db.Seed("ra", "a", Now.AddSeconds(-4));
        _db.Seed("rb", "b", Now.AddSeconds(-3));
        _db.Seed("rc", "c", Now.AddSeconds(-2));
        var d = _db.Seed("rd", "d", Now.AddSeconds(-1));
        var a = Probes.Hold("a");
        var b = Probes.Hold("b");
        var c = Probes.Hold("c");

        var cycle = RunCycle(lane);
        await Within(Probes.WhenStartedCountAsync(2));

        _clock.Advance(TimeSpan.FromSeconds(5));
        a.SetResult(Result.Success());
        await Within(Probes.WhenStartedCountAsync(3));
        Probes.StartedValues[2].Should().Be("c", "5 s is inside the 10 s budget");

        _clock.Advance(TimeSpan.FromSeconds(6));
        b.SetResult(Result.Success());
        c.SetResult(Result.Success());
        await Within(cycle);

        Probes.ValuesFor("rd").Should().BeEmpty("no row starts after the budget");
        var storedD = await _db.ReloadAsync(d.Id, Ct);
        storedD.ClaimedBy.Should().BeNull("a row that never started is released");
        storedD.ClaimedUntil.Should().BeNull();
        storedD.Attempts.Should().Be(0, "a release charges nothing");
        storedD.LastError.Should().BeNull();
        storedD.ProcessedOn.Should().BeNull();
        storedD.DeadLetteredOn.Should().BeNull();

        await lane.Lane.ProcessDueCommandsAsync(Ct);

        Probes.ValuesFor("rd").Should().Equal(["d"], "a released row is due again at once, with no backoff");
    }

    // L2.5, L2.12: the release is guarded by the claim token, so a row another replica has taken since the
    // batch claim is left untouched. Concurrency 1, budget 10 s: a takes 11 s and, meanwhile, another
    // replica takes c over; b is released, c keeps the other replica's claim.
    [Fact]
    public async Task Cycle_ReleaseGuard_LeavesARowAnotherReplicaTookUntouched()
    {
        var lane = CreateLane(maxConcurrency: 1, budget: Budget);
        var foreignReplica = Guid.NewGuid();
        var takenUntil = Now.AddMinutes(5);
        _db.Seed("ra", "a", Now.AddSeconds(-3));
        var b = _db.Seed("rb", "b", Now.AddSeconds(-2));
        var c = _db.Seed("rc", "c", Now.AddSeconds(-1));
        Probes.Behave("a", async (_, ct) =>
        {
            _clock.Advance(Budget + TimeSpan.FromSeconds(1));
            await _db.TakeOverAsync([c.Id], foreignReplica, takenUntil, ct);
            return Result.Success();
        });

        await lane.Lane.ProcessDueCommandsAsync(Ct);

        Probes.ValuesFor("rb").Should().BeEmpty();
        Probes.ValuesFor("rc").Should().BeEmpty();
        var storedB = await _db.ReloadAsync(b.Id, Ct);
        storedB.ClaimedBy.Should().BeNull("b was still this batch's, so it is released");
        storedB.ClaimedUntil.Should().BeNull();
        var storedC = await _db.ReloadAsync(c.Id, Ct);
        storedC.ClaimedBy.Should().Be(foreignReplica, "the guarded release matches nothing on a row another replica took");
        storedC.ClaimedUntil.Should().Be(takenUntil);
        storedC.Attempts.Should().Be(0);
    }

    // L2.6: more due work is reported when a full batch moved (a row progressed or was charged) OR when any row was released.
    // A partial batch (3 rows, BatchSize 10) reports more due work only because rows were released.
    [Fact]
    public async Task Cycle_ReleasedRows_ReportMoreDueWork()
    {
        var lane = CreateLane(maxConcurrency: 1, budget: Budget, batchSize: 10);
        _db.Seed("ra", "a", Now.AddSeconds(-3));
        _db.Seed("rb", "b", Now.AddSeconds(-2));
        _db.Seed("rc", "c", Now.AddSeconds(-1));
        Probes.Behave("a", (_, _) =>
        {
            _clock.Advance(Budget + TimeSpan.FromSeconds(1));
            return Task.FromResult(Result.Success());
        });

        var released = await lane.Lane.ProcessDueCommandsAsync(Ct);

        released.HasMoreDueWork.Should().BeTrue("two rows were released; the re-poll picks them up at once");
        Probes.StartedValues.Should().Equal("a");

        var drained = await lane.Lane.ProcessDueCommandsAsync(Ct);

        drained.HasMoreDueWork.Should().BeFalse("a partial batch with no release reports no more due work");
        Probes.StartedValues.Should().Equal("a", "b", "c");
    }

    // L2.7: an exception escaping a running row (here the completion stamp of "boom" fails, outside its
    // handler) stops new starts, waits for the rows still running, writes no release for the batch, and is
    // rethrown, so DrainAllAsync logs it for the source. The rows that never started recover at lease expiry.
    [Fact]
    public async Task Cycle_ARowThatThrowsOutsideTheHandler_StopsNewStarts_AwaitsTheRest_AndRethrows()
    {
        var logger = new StructuredRecordingLogger<InternalCommandLaneProcessor>();
        var lane = CreateLane(maxConcurrency: 2, logger: logger);
        var slow = _db.Seed("rs", "slow", Now.AddSeconds(-4));
        var boom = _db.Seed("rb", "boom", Now.AddSeconds(-3));
        var later1 = _db.Seed("r1", "later1", Now.AddSeconds(-2));
        var later2 = _db.Seed("r2", "later2", Now.AddSeconds(-1));
        var slowGate = Probes.Hold("slow");
        Probes.Behave("boom", (_, _) =>
        {
            // Armed after the renew, so the next UPDATE naming boom is its completion stamp.
            _db.Commands.FailNextUpdateOf(boom.Id);
            return Task.FromResult(Result.Success());
        });

        var cycle = RunCycle(lane);
        await Within(_db.Commands.Injected);

        cycle.IsCompleted.Should().BeFalse("the lane waits for slow, which is still running");

        slowGate.SetResult(Result.Success());
        var act = async () => await Within(cycle);
        await act.Should().NotThrowAsync("DrainAllAsync logs a failing source rather than failing the cycle");

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error)
            .Which.Exception.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().StartWith(LaneDatabaseCommandLog.InjectedFailureMessage);
        Probes.ValuesFor("r1").Should().BeEmpty("no row starts after the exception");
        Probes.ValuesFor("r2").Should().BeEmpty();
        (await _db.ReloadAsync(slow.Id, Ct)).ProcessedOn.Should().NotBeNull("the running row was awaited and finished");
        var storedBoom = await _db.ReloadAsync(boom.Id, Ct);
        storedBoom.ProcessedOn.Should().BeNull("its stamp is what failed");
        foreach (var never in new[] { later1, later2 })
        {
            var stored = await _db.ReloadAsync(never.Id, Ct);
            stored.ClaimedBy.Should().Be(storedBoom.ClaimedBy, "no release is written after an exception: the batch claim stays");
            stored.ClaimedBy.Should().NotBeNull();
            stored.Attempts.Should().Be(0);
        }

        // The fault was one-shot; boom succeeds from now on, so the recovery cycle is not cut short by it.
        Probes.Behave("boom", (_, _) => Task.FromResult(Result.Success()));
        _clock.Advance(TimeSpan.FromSeconds(_settings.LeaseSeconds + 1));
        await lane.Lane.ProcessDueCommandsAsync(Ct);

        Probes.ValuesFor("r1").Should().Equal(["later1"], "the unfinished rows come back at lease expiry");
        Probes.ValuesFor("r2").Should().Equal("later2");
    }

    // L2.8: the stopping token reaches every running row, the lane awaits them all before returning, and
    // nothing is released after cancellation, even with the budget already elapsed (which would otherwise
    // release the row that never started).
    [Fact]
    public async Task Cycle_Shutdown_CancelsEveryRunningRow_AndWritesNoRelease()
    {
        var lane = CreateLane(maxConcurrency: 2, budget: Budget);
        var a = _db.Seed("ra", "a", Now.AddSeconds(-3));
        var b = _db.Seed("rb", "b", Now.AddSeconds(-2));
        var c = _db.Seed("rc", "c", Now.AddSeconds(-1));
        foreach (var value in new[] { "a", "b" })
        {
            Probes.Behave(value, async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, ct);
                return Result.Success();
            });
        }

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var cycle = Task.Run(() => lane.Lane.ProcessDueCommandsAsync(stopping.Token), Ct);
        await Within(Probes.WhenStartedCountAsync(2));
        _clock.Advance(Budget + TimeSpan.FromSeconds(1));
        await stopping.CancelAsync();

        var act = async () => await Within(cycle);
        await act.Should().ThrowAsync<OperationCanceledException>("shutdown is rethrown, never recorded as a failure");

        Probes.FinishedValues.Should().BeEquivalentTo(BothRunningRows, "the lane awaited every running row before it returned");
        var storedA = await _db.ReloadAsync(a.Id, Ct);
        storedA.ClaimedBy.Should().NotBeNull("a cancelled row stays leased");
        foreach (var row in new[] { storedA, await _db.ReloadAsync(b.Id, Ct), await _db.ReloadAsync(c.Id, Ct) })
        {
            row.Attempts.Should().Be(0, "shutdown charges nothing");
            row.LastError.Should().BeNull();
            row.ClaimedBy.Should().Be(storedA.ClaimedBy, "every row keeps the batch claim: nothing is released after cancellation");
        }

        Probes.ValuesFor("rc").Should().BeEmpty();
    }

    // L2.9: internal_commands.lane.released.count (tag lane only) counts released rows, and the per-batch
    // Debug line of a batch that charged rows also carries the released count. Concurrency 1, budget 10 s:
    // x1 (cooling receiver) is charged, a takes 11 s, b and c are released.
    [Fact]
    public async Task Cycle_CountsReleases()
    {
        const string laneName = "probe-releases";
        var logger = new StructuredRecordingLogger<InternalCommandLaneProcessor>();
        var lane = CreateLane(maxConcurrency: 1, budget: Budget, logger: logger, laneName: laneName);
        await _db.OpenCooldownAsync(lane, "x", _settings.MaxAttempts, Ct);
        _db.Seed("x", "x1", Now.AddSeconds(-4));
        _db.Seed("ra", "a", Now.AddSeconds(-3));
        _db.Seed("rb", "b", Now.AddSeconds(-2));
        _db.Seed("rc", "c", Now.AddSeconds(-1));
        Probes.Behave("a", (_, _) =>
        {
            _clock.Advance(Budget + TimeSpan.FromSeconds(1));
            return Task.FromResult(Result.Success());
        });
        var linesBefore = logger.Entries.Count;
        using var meters = new InternalCommandMeterRecorder();

        await lane.Lane.ProcessDueCommandsAsync(Ct);

        var released = meters.For("internal_commands.lane.released.count")
            .Where(m => m.Tag("lane") == laneName)
            .ToList();
        released.Sum(m => m.Value).Should().Be(2, "b and c were released");
        released.Should().AllSatisfy(m => m.Tags.Keys.Should().Equal("lane"));

        var sourceName = new TenantDataSourceTarget(DataSourceKey.Default(DataSource.Sqlite), null).ToString();
        var debug = logger.Entries.Skip(linesBefore).Where(e => e.Level == LogLevel.Debug)
            .Should().ContainSingle("one Debug line per batch").Subject;
        debug.CarriesText(laneName).Should().BeTrue();
        debug.CarriesText(sourceName).Should().BeTrue();
        debug.CarriesNumber(1).Should().BeTrue("the batch charged one row");
        debug.CarriesNumber(2).Should().BeTrue("and released two");
    }

    // L2.10: a lane batch on one target lasts at most the start budget plus one attempt: no row starts at
    // or after first start + budget. Concurrency 2, budget 10 s, six rows: r1 and r2 start at T0, r3 at
    // T0+6 when r1 frees its slot, and at T0+12 nothing else starts; r4 to r6 are released.
    [Fact]
    public async Task Cycle_ABatchNeverOutlivesTheBudgetPlusOneAttempt()
    {
        var lane = CreateLane(maxConcurrency: 2, budget: Budget);
        var gates = new Dictionary<string, TaskCompletionSource<Result>>(StringComparer.Ordinal);
        var rows = new List<InternalCommandMessage>();
        for (var index = 1; index <= 6; index++)
        {
            var value = string.Create(CultureInfo.InvariantCulture, $"r{index}");
            rows.Add(_db.Seed(value, value, Now.AddSeconds(-10 + index)));
            gates[value] = Probes.Hold(value);
        }

        var firstStart = _clock.GetUtcNow();
        var cycle = RunCycle(lane);
        await Within(Probes.WhenStartedCountAsync(2));

        _clock.Advance(TimeSpan.FromSeconds(6));
        gates["r1"].SetResult(Result.Success());
        await Within(Probes.WhenStartedCountAsync(3));

        _clock.Advance(TimeSpan.FromSeconds(6));
        gates["r2"].SetResult(Result.Success());
        gates["r3"].SetResult(Result.Success());
        await Within(cycle);

        Probes.Invocations.Should().AllSatisfy(i => (i.At - firstStart).Should().BeLessThan(Budget, "no row starts after the budget"));
        Probes.StartedValues.Should().BeEquivalentTo("r1", "r2", "r3");
        foreach (var row in rows.Skip(3))
        {
            (await _db.ReloadAsync(row.Id, Ct)).ClaimedBy.Should().BeNull("the rows that never started are released");
        }
    }

    // L2.10: one failing receiver occupies at most one slot, leaving DeliveryConcurrency - 1 slots to
    // everyone else. x is a black hole (x1 is held); x2 to x5 wait for x1; the three y rows behind them run
    // to completion in the other slot while x1 is still held. When x1 finally fails, x2 to x5 are charged
    // at their turn and never sent.
    [Fact]
    public async Task Cycle_ABlackHoleReceiver_LeavesTheOtherSlotsWorking()
    {
        var lane = CreateLane(maxConcurrency: 2, budget: TimeSpan.FromSeconds(30));
        var xRows = Enumerable.Range(1, 5)
            .Select(index => _db.Seed("x", string.Create(CultureInfo.InvariantCulture, $"x{index}"), Now.AddSeconds(-20 + index)))
            .ToList();
        var yRows = Enumerable.Range(1, 3)
            .Select(index => _db.Seed("y", string.Create(CultureInfo.InvariantCulture, $"y{index}"), Now.AddSeconds(-10 + index)))
            .ToList();
        var blackHole = Probes.Hold("x1");

        var cycle = RunCycle(lane);
        await Within(Probes.WhenFinishedAsync("y3"));

        Probes.ValuesFor("x").Should().Equal(["x1"], "the black hole holds one slot");
        Probes.ValuesFor("y").Should().Equal(["y1", "y2", "y3"], "the other slot kept working");
        cycle.IsCompleted.Should().BeFalse("x1 is still running");

        blackHole.SetResult(ConcurrentProbeLog.FailedDelivery());
        await Within(cycle);

        Probes.ValuesFor("x").Should().Equal(["x1"], "x2 to x5 were charged at their turn, never sent");
        foreach (var row in xRows.Skip(1))
        {
            (await _db.ReloadAsync(row.Id, Ct)).LastError.Should().Be(InternalCommandLaneDatabase.CoolingDownError);
        }

        foreach (var row in yRows)
        {
            (await _db.ReloadAsync(row.Id, Ct)).ProcessedOn.Should().NotBeNull();
        }
    }

    private static Task Within(Task task) => task.WaitAsync(SafetyTimeout, TimeProvider.System, Ct);

    private static Task<InternalCommandCycleResult> RunCycle(LaneUnderTest lane) =>
        Task.Run(() => lane.Lane.ProcessDueCommandsAsync(Ct), Ct);

    private LaneUnderTest CreateLane(
        int maxConcurrency,
        TimeSpan? budget = null,
        int batchSize = 50,
        ILogger<InternalCommandLaneProcessor>? logger = null,
        string laneName = InternalCommandLaneDatabase.DefaultLaneName)
    {
        _settings = InternalCommandTestHarness.Settings(maxAttempts: 5, batchSize: batchSize);
        return _db.CreateLane(
            _settings,
            InternalCommandLaneDatabase.Lane(maxConcurrency, budget ?? TimeSpan.FromSeconds(30), name: laneName),
            logger);
    }
}

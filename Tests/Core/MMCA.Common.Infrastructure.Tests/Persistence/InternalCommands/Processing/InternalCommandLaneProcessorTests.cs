using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.InternalCommands;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.InternalCommands;
using MMCA.Common.Infrastructure.Persistence.InternalCommands.Administration;
using MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;
using MMCA.Common.Infrastructure.Persistence.Tenancy;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using MMCA.Common.Shared.Abstractions;
using static MMCA.Common.Infrastructure.Tests.Persistence.InternalCommands.InternalCommandLaneTestHarness;

namespace MMCA.Common.Infrastructure.Tests.Persistence.InternalCommands.Processing;

/// <summary>
/// Tests for <see cref="InternalCommandLaneProcessor"/>, Phase 1 of the webhook delivery lane
/// (design-webhook-lane.md 1(b) rules L1.4 to L1.22, 1(c)): the lane claims only its own command type
/// with the shared pending predicate, runs one row at a time, cools a failing receiver down on this
/// replica, and charges each due row of a cooling receiver one failed attempt through the unchanged
/// failure path without sending it. Every timeline is driven by a <see cref="FakeTimeProvider"/>.
/// </summary>
public sealed class InternalCommandLaneProcessorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly InternalCommandTestHarness.InternalCommandTestContext _context;
    private readonly FakeTimeProvider _clock = new(InternalCommandTestHarness.Epoch);
    private readonly LaneProbeLog _probes;
    private readonly ExecutionLog _shared = new();
    private readonly List<IDisposable> _disposables = [];
    private InternalCommandsSettings _settings = InternalCommandTestHarness.Settings(maxAttempts: 5);

    public InternalCommandLaneProcessorTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = InternalCommandTestHarness.InternalCommandTestContext.Create(_connection);
        _probes = new LaneProbeLog(_clock);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _context.Dispose();
        _connection.Dispose();
    }

    // L1.4: the lane claims only rows whose CommandType is its own; a shared-queue row stays untouched.
    [Fact]
    public async Task Cycle_ClaimsOnlyRowsOfTheLaneCommandType()
    {
        var lane = CreateLane();
        var shared = SeedCommand(new RecordingCommand("shared"), Now.AddSeconds(-30));
        var delivery = Seed("r1", "lane-row", Now.AddSeconds(-10));

        await lane.Lane.ProcessDueCommandsAsync(Ct);

        _probes.ValuesFor("r1").Should().Equal("lane-row");
        _shared.Executions.Should().BeEmpty("a shared-queue row is never the lane's to run");
        var storedShared = await ReloadAsync(shared.Id);
        storedShared.Attempts.Should().Be(0);
        storedShared.ClaimedBy.Should().BeNull("the lane must not even claim a row it will not run");
        storedShared.ProcessedOn.Should().BeNull();
        (await ReloadAsync(delivery.Id)).ProcessedOn.Should().Be(Now);
    }

    // L1.4, 1(f): the lane's copy of the pending predicate is pinned to the shared one. The truth table
    // below IS the shared predicate (InternalCommandProcessor.cs:240-243): not processed, not
    // dead-lettered, attempts below MaxAttempts, lease null or expired. The shared loop's side of it is
    // pinned by the existing processor tests (Cycle_ARowUnderAnotherReplicasUnexpiredLease_IsNotRun,
    // Cycle_ARowWhoseLeaseHasExpired_IsReclaimedAndRun, Cycle_ADeadLetteredRow_IsNeverClaimedAgain). The
    // shared processor is deliberately not run here: its depth gauge is process-wide, and
    // InternalCommandProcessorTests asserts that gauge from a parallel test class.
    // DESIGN-QUESTION: 1(c) asks for "the lane claims exactly what the shared loop would"; that is
    // pinned here against the shared predicate's truth table, not by running both loops side by side.
    [Theory]
    [InlineData("fresh", true)]
    [InlineData("lease_expired", true)]
    [InlineData("leased", false)]
    [InlineData("processed", false)]
    [InlineData("dead_lettered", false)]
    [InlineData("exhausted", false)]
    public async Task Cycle_UsesTheSharedPendingPredicate(string state, bool claimable)
    {
        var lane = CreateLane();
        var row = state switch
        {
            "lease_expired" => Seed("r1", state, Now.AddSeconds(-10), claimedUntil: Now.AddMinutes(-1), claimedBy: Guid.NewGuid()),
            "leased" => Seed("r1", state, Now.AddSeconds(-10), claimedUntil: Now.AddMinutes(5), claimedBy: Guid.NewGuid()),
            "exhausted" => Seed("r1", state, Now.AddSeconds(-10), attempts: _settings.MaxAttempts),
            _ => Seed("r1", state, Now.AddSeconds(-10)),
        };

        var marked = Now.AddMinutes(-1);
        if (string.Equals(state, "processed", StringComparison.Ordinal))
        {
            await _context.Set<InternalCommandMessage>().Where(c => c.Id == row.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.ProcessedOn, (DateTime?)marked), Ct);
        }
        else if (string.Equals(state, "dead_lettered", StringComparison.Ordinal))
        {
            await _context.Set<InternalCommandMessage>().Where(c => c.Id == row.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.DeadLetteredOn, (DateTime?)marked), Ct);
        }

        _context.ChangeTracker.Clear();

        await lane.Lane.ProcessDueCommandsAsync(Ct);

        _probes.ValuesFor("r1").Should().HaveCount(claimable ? 1 : 0, $"a {state} row is {(claimable ? string.Empty : "not ")}pending for the shared loop either");
    }

    // L1.6: one row at a time per replica, in ScheduledOn then Id order.
    [Fact]
    public async Task Cycle_RunsRowsOneAtATimeInScheduledOrder()
    {
        var lane = CreateLane();
        foreach (var receiver in new[] { "r1", "r2", "r3" })
        {
            _probes.Behave(receiver, async (_, _) =>
            {
                // Yield so an overlapping second row would be observable as two in flight.
                await Task.Yield();
                return Result.Success();
            });
        }

        Seed("r2", "second", Now.AddSeconds(-20));
        Seed("r3", "third", Now.AddSeconds(-10));
        Seed("r1", "first", Now.AddSeconds(-30));

        await lane.Lane.ProcessDueCommandsAsync(Ct);

        _probes.Invocations.Select(i => i.Value).Should().Equal("first", "second", "third");
        _probes.MaxInFlight.Should().Be(1, "Phase 1 sends one delivery at a time per replica");
    }

    // L1.7: a row that cannot be deserialized has no key and dead-letters exactly as today; it neither
    // opens a cooldown nor is charged, so the receiver's next row is sent.
    [Fact]
    public async Task Cycle_ARowWithAnUnreadablePayload_DeadLettersAsToday()
    {
        var lane = CreateLane();
        var broken = Seed("r1", "broken", Now.AddSeconds(-10));
        var healthy = Seed("r1", "healthy", Now);
        await _context.Set<InternalCommandMessage>()
            .Where(c => c.Id == broken.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Payload, "{\"Receiver\":"), Ct);
        _context.ChangeTracker.Clear();

        await lane.Lane.ProcessDueCommandsAsync(Ct);

        var storedBroken = await ReloadAsync(broken.Id);
        storedBroken.DeadLetteredOn.Should().Be(Now);
        storedBroken.LastError.Should().Contain("deserialized");
        storedBroken.ClaimedBy.Should().BeNull();
        _probes.ValuesFor("r1").Should().Equal("healthy");
        (await ReloadAsync(healthy.Id)).ProcessedOn.Should().Be(Now);
    }

    // L1.8: a real attempt that fails, by Result.Failure or by an exception, cools its receiver down,
    // and another receiver is unaffected.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cycle_AFailedRealAttempt_OpensTheCooldown(bool throws)
    {
        var lane = CreateLane();
        if (throws)
        {
            _probes.Throw("x");
        }
        else
        {
            _probes.Fail("x");
        }

        Seed("x", "first", Now.AddSeconds(-10));
        await lane.Lane.ProcessDueCommandsAsync(Ct);
        _probes.ValuesFor("x").Should().Equal("first");

        var second = Seed("x", "second", Now);
        Seed("y", "other", Now);
        await lane.Lane.ProcessDueCommandsAsync(Ct);

        _probes.ValuesFor("x").Should().Equal(["first"], "x is cooling down, so its next due row is not sent");
        _probes.ValuesFor("y").Should().Equal("other");
        (await ReloadAsync(second.Id)).LastError.Should().Be(CoolingDownError);
    }

    // L1.8: success and the outcomes that are not a failed attempt never open a cooldown: the second
    // row of the same receiver is treated on its own merits, never charged.
    [Theory]
    [InlineData("success")]
    [InlineData("handler_missing")]
    [InlineData("type_unresolvable")]
    [InlineData("payload_invalid")]
    public async Task Cycle_SuccessAndNonReceiverOutcomes_NeverOpenACooldown(string outcome)
    {
        const string retiredType = "Tests.Retired.LaneProbe.v1";
        var lane = outcome switch
        {
            "handler_missing" => CreateLane(registerHandler: false),
            "type_unresolvable" => CreateLane(lane: ProbeLane(commandType: retiredType)),
            _ => CreateLane(),
        };

        var first = Seed("x", "first", Now.AddSeconds(-10));
        var second = Seed("x", "second", Now);

        if (string.Equals(outcome, "type_unresolvable", StringComparison.Ordinal))
        {
            await _context.Set<InternalCommandMessage>()
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.CommandType, retiredType), Ct);
        }
        else if (string.Equals(outcome, "payload_invalid", StringComparison.Ordinal))
        {
            await _context.Set<InternalCommandMessage>()
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Payload, "{\"Receiver\":"), Ct);
        }

        _context.ChangeTracker.Clear();

        await lane.Lane.ProcessDueCommandsAsync(Ct);

        var storedFirst = await ReloadAsync(first.Id);
        var storedSecond = await ReloadAsync(second.Id);
        storedSecond.LastError.Should().NotBe(CoolingDownError, "the first row's outcome must not cool x down");

        switch (outcome)
        {
            case "success":
                _probes.ValuesFor("x").Should().Equal("first", "second");
                storedFirst.ProcessedOn.Should().Be(Now);
                storedSecond.ProcessedOn.Should().Be(Now);
                break;
            case "handler_missing":
                storedFirst.LastError.Should().Contain("No command handler is registered");
                storedSecond.LastError.Should().Contain("No command handler is registered");
                break;
            case "type_unresolvable":
                storedFirst.LastError.Should().Contain("Cannot resolve command type");
                storedSecond.LastError.Should().Contain("Cannot resolve command type");
                break;
            default:
                storedFirst.LastError.Should().Contain("deserialized");
                storedSecond.LastError.Should().Contain("deserialized");
                break;
        }
    }

    // L1.8: a charged attempt never opens or extends the cooldown. Opened at T0 for 60 s; a row charged
    // at T0+30 must not push the end to T0+90, so at T0+60 the first row reached runs for real.
    [Fact]
    public async Task Cycle_ChargedAttempts_NeverOpenOrExtendTheCooldown()
    {
        var lane = CreateLane();
        await OpenCooldownAsync(lane, "x");
        var opened = Now;

        _probes.Fail("x");
        _clock.Advance(TimeSpan.FromSeconds(30));
        var charged = Seed("x", "charged", Now);
        await lane.Lane.ProcessDueCommandsAsync(Ct);
        (await ReloadAsync(charged.Id)).LastError.Should().Be(CoolingDownError);

        _probes.Succeed("x");
        _clock.Advance(opened.AddSeconds(60) - Now);
        Seed("x", "after", Now);
        await lane.Lane.ProcessDueCommandsAsync(Ct);

        _probes.ValuesFor("x").Should().Equal(
            ["poison-x", "charged", "after"],
            "the charged row, re-leased for its 8 to 12 s backoff, is due again and is the first row reached: the trial");
    }

    // L1.9: a row of a cooling receiver is charged without a renew, an execution scope or the handler.
    [Fact]
    public async Task Cycle_ARowOfACoolingReceiver_IsChargedWithoutRenewOrHandler()
    {
        var lane = CreateLane();
        await OpenCooldownAsync(lane, "x");
        var scopesBefore = _probes.ExecutionScopes;
        var row = Seed("x", "cooling", Now);
        using var recorder = new EfCommandRecorder(_context);

        await lane.Lane.ProcessDueCommandsAsync(Ct);

        _probes.ValuesFor("x").Should().Equal(["poison-x"], "a charged row is never sent");
        _probes.ExecutionScopes.Should().Be(scopesBefore, "a charged row opens no execution scope");
        recorder.Commands.Count(EfCommandRecorder.IsUpdate).Should().Be(
            2, "the batch claim and the charged stamp; a renew would be a third UPDATE");
        var stored = await ReloadAsync(row.Id);
        stored.Attempts.Should().Be(1);
        stored.LastError.Should().Be(CoolingDownError);
    }

    // L1.9, L1.10: the charged row keeps today's timeline: one attempt, the attempt-1 backoff
    // (10 s x [0.8, 1.2]), the token kept, and the fixed LastError in the DescribeErrors format,
    // logged by the unchanged failure path's own retry line.
    [Fact]
    public async Task Cycle_AChargedRow_RetriesWithTheNormalBackoffAndTheFixedLastError()
    {
        var rowsLogger = new RecordingLogger<InternalCommandProcessor>();
        var lane = CreateLane(rowsLogger: rowsLogger);
        await OpenCooldownAsync(lane, "x");
        var row = Seed("x", "cooling", Now);

        await lane.Lane.ProcessDueCommandsAsync(Ct);

        var stored = await ReloadAsync(row.Id);
        stored.Attempts.Should().Be(1);
        stored.LastError.Should().Be(CoolingDownError);
        stored.ProcessedOn.Should().BeNull();
        stored.DeadLetteredOn.Should().BeNull("one charged attempt of five is a retry");
        stored.ClaimedBy.Should().NotBeNull("a retry is parked under the token it was claimed with");
        stored.ClaimedUntil.Should().NotBeNull();
        stored.ClaimedUntil!.Value.Should().BeOnOrAfter(Now.AddSeconds(8)).And.BeOnOrBefore(Now.AddSeconds(12));
        rowsLogger.Entries.Should().ContainSingle(e => e.EventName == "LogAttemptFailed")
            .Which.Message.Should().Contain(CoolingDownError).And.Contain("attempt 1");
    }

    // L1.10, L1.16: a charged row on its last attempt dead-letters attempts_exhausted like any failed attempt.
    [Fact]
    public async Task Cycle_AChargedRowOnItsLastAttempt_DeadLettersAttemptsExhausted()
    {
        var lane = CreateLane();
        await OpenCooldownAsync(lane, "x");
        var row = Seed("x", "last", Now, attempts: _settings.MaxAttempts - 1);
        using var meters = new InternalCommandMeterRecorder();

        await lane.Lane.ProcessDueCommandsAsync(Ct);

        var stored = await ReloadAsync(row.Id);
        stored.Attempts.Should().Be(_settings.MaxAttempts);
        stored.DeadLetteredOn.Should().Be(Now);
        stored.LastError.Should().Be(CoolingDownError);
        stored.ClaimedBy.Should().BeNull("a dead-lettered row releases its lease");
        _probes.ValuesFor("x").Should().Equal("poison-x");
        meters.For("internal_commands.dead_letter.count")
            .Where(m => m.Tag("command_type") == ProbeCommandType)
            .Should().ContainSingle()
            .Which.Tag("reason").Should().Be("attempts_exhausted");
    }

    // L1.10, recovery guarantee: the key's cooldown opens just before the row is scheduled and the
    // receiver answers successfully from then on. Whatever the jitter, the row gets a real attempt
    // before it can dead-letter (at the defaults the shortest retry span is 120 s against a 60 s
    // cooldown). Ten independent trials, each on a fresh lane, sample the jitter.
    [Fact]
    public async Task Cycle_AReceiverHealthyAtScheduling_GetsARealAttemptBeforeDeadLetter()
    {
        for (var trial = 0; trial < 10; trial++)
        {
            var lane = CreateLane();
            var receiver = $"healthy-{trial}";
            await OpenCooldownAsync(lane, receiver);
            var row = Seed(receiver, "scheduled-after-the-failure", Now);

            for (var step = 0; step < 20; step++)
            {
                await lane.Lane.ProcessDueCommandsAsync(Ct);
                var current = await ReloadAsync(row.Id);
                if (current.ProcessedOn is not null || current.DeadLetteredOn is not null)
                {
                    break;
                }

                current.ClaimedUntil.Should().NotBeNull("an unfinished row is parked for its backoff");
                _clock.Advance(current.ClaimedUntil!.Value - Now);
            }

            var stored = await ReloadAsync(row.Id);
            stored.DeadLetteredOn.Should().BeNull($"trial {trial}: a receiver healthy at scheduling must get a real attempt");
            stored.ProcessedOn.Should().NotBeNull($"trial {trial}");
            _probes.ValuesFor(receiver).Should().Contain("scheduled-after-the-failure");
        }
    }

    // L1.11: the first row of the key reached at or after the cooldown end runs for real (the trial),
    // and its success closes the key so the rows behind it run too. One second earlier a row is charged.
    [Fact]
    public async Task Cycle_AfterTheCooldown_TheFirstRowReachedRunsAsATrial_AndSuccessCloses()
    {
        var lane = CreateLane();
        await OpenCooldownAsync(lane, "x");
        var opened = Now;

        _clock.Advance(TimeSpan.FromSeconds(59));
        var early = Seed("x", "early", Now);
        await lane.Lane.ProcessDueCommandsAsync(Ct);
        (await ReloadAsync(early.Id)).LastError.Should().Be(CoolingDownError, "59 s is still inside the cooldown");

        _clock.Advance(opened.AddSeconds(60) - Now);
        var trial = Seed("x", "trial", Now.AddSeconds(-2));
        var next = Seed("x", "next", Now.AddSeconds(-1));
        var last = Seed("x", "last", Now);

        await lane.Lane.ProcessDueCommandsAsync(Ct);

        _probes.ValuesFor("x").Should().Equal("poison-x", "trial", "next", "last");
        (await ReloadAsync(trial.Id)).ProcessedOn.Should().Be(Now);
        (await ReloadAsync(next.Id)).ProcessedOn.Should().Be(Now);
        (await ReloadAsync(last.Id)).ProcessedOn.Should().Be(Now);
    }

    // L1.11, L1.8: a failed trial reopens the key for a FULL cooldown measured from the trial's end.
    // The trial takes 5 s (T0+60 to T0+65), so the key cools until T0+125: charged at T0+124, trial at T0+125.
    [Fact]
    public async Task Cycle_AFailedTrial_ReopensForAFullCooldown()
    {
        var lane = CreateLane();
        await OpenCooldownAsync(lane, "x");
        var opened = Now;
        _probes.Behave("x", (_, _) =>
        {
            _clock.Advance(TimeSpan.FromSeconds(5));
            return Task.FromResult(LaneProbeLog.FailedDelivery());
        });

        _clock.Advance(opened.AddSeconds(60) - Now);
        Seed("x", "trial", Now.AddSeconds(-1));
        var behind = Seed("x", "behind", Now);
        await lane.Lane.ProcessDueCommandsAsync(Ct);

        _probes.ValuesFor("x").Should().Equal("poison-x", "trial");
        (await ReloadAsync(behind.Id)).LastError.Should().Be(CoolingDownError, "the failed trial reopened the key");

        _clock.Advance(opened.AddSeconds(124) - Now);
        var stillCooling = Seed("x", "still-cooling", Now);
        await lane.Lane.ProcessDueCommandsAsync(Ct);

        _probes.ValuesFor("x").Should().Equal(["poison-x", "trial"], "T0+124 is inside the cooldown that runs from the trial's end");
        (await ReloadAsync(stillCooling.Id)).LastError.Should().Be(CoolingDownError);

        _clock.Advance(opened.AddSeconds(125) - Now);
        Seed("x", "second-trial", Now);
        await lane.Lane.ProcessDueCommandsAsync(Ct);

        _probes.ValuesFor("x").Should().Equal("poison-x", "trial", "second-trial");
    }

    // L1.12: a full batch made only of charged rows reports more due work, so a black-hole backlog
    // larger than one batch is charged through in consecutive cycles.
    [Fact]
    public async Task Cycle_AFullBatchOfChargedRows_ReportsMoreDueWork()
    {
        var lane = CreateLane(batchSize: 3);
        await OpenCooldownAsync(lane, "x");
        var rows = new[]
        {
            Seed("x", "a", Now.AddSeconds(-3)),
            Seed("x", "b", Now.AddSeconds(-2)),
            Seed("x", "c", Now.AddSeconds(-1)),
        };

        var cycle = await lane.Lane.ProcessDueCommandsAsync(Ct);

        cycle.HasMoreDueWork.Should().BeTrue("dueCount == BatchSize and chargedCount > 0");
        _probes.ValuesFor("x").Should().Equal("poison-x");
        foreach (var row in rows)
        {
            var stored = await ReloadAsync(row.Id);
            stored.Attempts.Should().Be(1);
            stored.LastError.Should().Be(CoolingDownError);
        }
    }

    // L1.13 (the Cycle_ARowAnotherReplicaReclaimedMidBatch_IsNotRun shape): another replica takes over two
    // rows while an earlier row of the batch runs. The row that would be sent fails its renew and is
    // skipped; the row that would be charged is not stamped, because the stamp is token-guarded.
    [Fact]
    public async Task Cycle_ARowWhoseClaimWasTakenOver_IsNeitherSentNorStamped()
    {
        var lane = CreateLane();
        await OpenCooldownAsync(lane, "x");
        var foreignReplica = Guid.NewGuid();
        var takenUntil = Now.AddMinutes(5);
        Seed("y", "first", Now.AddSeconds(-3));
        var toSend = Seed("z", "to-send", Now.AddSeconds(-2));
        var toCharge = Seed("x", "to-charge", Now.AddSeconds(-1));
        _probes.Behave("y", async (_, ct) =>
        {
            await _context.Set<InternalCommandMessage>()
                .Where(c => c.Id == toSend.Id || c.Id == toCharge.Id)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(c => c.ClaimedBy, foreignReplica)
                        .SetProperty(c => c.ClaimedUntil, takenUntil),
                    ct);
            return Result.Success();
        });

        await lane.Lane.ProcessDueCommandsAsync(Ct);

        _probes.ValuesFor("y").Should().Equal("first");
        _probes.ValuesFor("z").Should().BeEmpty("a row whose renew fails is never sent");
        _probes.ValuesFor("x").Should().Equal("poison-x");
        foreach (var taken in new[] { toSend, toCharge })
        {
            var stored = await ReloadAsync(taken.Id);
            stored.ClaimedBy.Should().Be(foreignReplica, "the row is the other replica's now");
            stored.Attempts.Should().Be(0, "nothing was stamped on a row this replica no longer owns");
            stored.LastError.Should().BeNull();
            stored.ProcessedOn.Should().BeNull();
        }
    }

    // L1.14: the stopping token reaches the row being sent; it stays leased and is charged nothing, and
    // the rest of the batch stays leased until lease expiry.
    [Fact]
    public async Task Cycle_Shutdown_LeavesTheRowLeasedAndChargesNothing()
    {
        var lane = CreateLane();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _probes.Behave("x", async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Result.Success();
        });
        var sending = Seed("x", "sending", Now.AddSeconds(-2));
        var behind = Seed("y", "behind", Now.AddSeconds(-1));
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var cycle = Task.Run(() => lane.Lane.ProcessDueCommandsAsync(stopping.Token), Ct);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, Ct);
        await stopping.CancelAsync();

        var act = async () => await cycle.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, Ct);
        await act.Should().ThrowAsync<OperationCanceledException>("shutdown is rethrown, never recorded as a failure");

        var storedSending = await ReloadAsync(sending.Id);
        storedSending.Attempts.Should().Be(0, "shutdown charges no attempt");
        storedSending.LastError.Should().BeNull();
        storedSending.ProcessedOn.Should().BeNull();
        storedSending.DeadLetteredOn.Should().BeNull();
        storedSending.ClaimedBy.Should().NotBeNull("the row stays leased and comes back at lease expiry");
        var storedBehind = await ReloadAsync(behind.Id);
        storedBehind.Attempts.Should().Be(0);
        storedBehind.ClaimedBy.Should().Be(storedSending.ClaimedBy, "the rest of the batch keeps the batch claim");
        _probes.ValuesFor("y").Should().BeEmpty();
    }

    // L1.15: an exception escaping a row outside the handler (here the completion stamp fails because
    // the table vanished) propagates out of the lane's per-target step, DrainAllAsync logs it for that
    // source, and the rest of the batch recovers at lease expiry.
    [Fact]
    public async Task Cycle_AnExceptionOutsideTheHandler_PropagatesAndLeavesTheRestLeased()
    {
        var logger = new RecordingLogger<InternalCommandLaneProcessor>();
        var lane = CreateLane(logger: logger);
        var first = Seed("x", "first", Now.AddSeconds(-2));
        var rest = Seed("y", "rest", Now.AddSeconds(-1));
        _probes.Behave("x", async (_, ct) =>
        {
            await _context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"InternalCommands\" RENAME TO \"InternalCommandsAway\"", ct);
            return Result.Success();
        });

        var act = async () => await lane.Lane.ProcessDueCommandsAsync(Ct);
        await act.Should().NotThrowAsync("DrainAllAsync isolates a failing source, exactly as for the shared loop");

        await _context.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"InternalCommandsAway\" RENAME TO \"InternalCommands\"", Ct);
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error && e.Exception is SqliteException);
        _probes.ValuesFor("y").Should().BeEmpty("the exception ended the batch");
        var storedFirst = await ReloadAsync(first.Id);
        storedFirst.ProcessedOn.Should().BeNull("its stamp is what failed");
        storedFirst.ClaimedBy.Should().NotBeNull();
        var storedRest = await ReloadAsync(rest.Id);
        storedRest.Attempts.Should().Be(0);
        storedRest.ClaimedBy.Should().Be(storedFirst.ClaimedBy, "the rest stays under the batch claim");

        _probes.Succeed("x");
        _clock.Advance(TimeSpan.FromSeconds(_settings.LeaseSeconds + 1));
        await lane.Lane.ProcessDueCommandsAsync(Ct);

        _probes.ValuesFor("y").Should().Equal(["rest"], "the rest of the batch comes back at lease expiry");
    }

    // L1.16: the lane records its own instruments, tagged lane (plus data_source on the age gauge),
    // never the shared gauges, and no instrument carries a receiver key. The shared oldest-due gauge is
    // checked on a per-tenant source name no other test writes (a sentinel set before the cycle must
    // survive it). DESIGN-QUESTION: the shared DEPTH gauge is one process-wide value with no key, so
    // "never the shared gauges" cannot be checked for it here without racing InternalCommandProcessorTests.
    [Fact]
    public async Task Cycle_RecordsLaneInstruments_AndNeverTheSharedGauges()
    {
        const string tenantId = "lane-metrics-probe";
        const double sentinel = -4242;
        var sharedKey = DataSourceKey.Default(DataSource.Sqlite);
        var tenancy = new TenancySettings();
        var tenant = new TenantEntrySettings();
        tenant.DataSources.Add(sharedKey.Name, new TenantDataSourceOverrideSettings { SqliteConnectionString = "DataSource=:memory:" });
        tenancy.Tenants.Add(tenantId, tenant);
        var sharedSource = new TenantDataSourceTarget(sharedKey, null).ToString();
        var tenantSource = new TenantDataSourceTarget(sharedKey, tenantId).ToString();

        InternalCommandMetrics.SetOldestDueAge(tenantSource, sentinel);
        using var meters = new InternalCommandMeterRecorder();
        var lane = CreateLane(tenancy: tenancy);
        await OpenCooldownAsync(lane, "x");
        Seed("x", "charged", Now);

        await lane.Lane.ProcessDueCommandsAsync(Ct);
        meters.RecordObservableInstruments();

        meters.For("internal_commands.lane.short_circuited.count").Where(IsProbeLane)
            .Sum(m => m.Value).Should().Be(1, "one charged attempt");
        meters.For("internal_commands.lane.receiver_cooldowns.count").Where(IsProbeLane)
            .Sum(m => m.Value).Should().Be(1, "one closed-to-open transition");
        var depth = meters.For("internal_commands.lane.pending.depth").Where(IsProbeLane).ToList();
        depth.Should().NotBeEmpty();
        depth[^1].Value.Should().Be(1, "the charged row was the only lane row pending at the start of the cycle");
        depth.Should().AllSatisfy(m => m.Tags.Keys.Should().Equal("lane"));
        var ages = meters.For("internal_commands.lane.oldest_due.age").Where(IsProbeLane).ToList();
        ages.Select(m => m.Tag("data_source")).Should().Contain(sharedSource).And.Contain(tenantSource);
        meters.For("internal_commands.oldest_due.age")
            .Where(m => m.Tag("data_source") == tenantSource)
            .Should().ContainSingle()
            .Which.Value.Should().Be(sentinel, "the lane never sets the shared oldest-due gauge");

        string[] allowedKeys = ["lane", "data_source", "command_type", "reason"];
        meters.All.Should().AllSatisfy(m => m.Tags.Keys.Should().BeSubsetOf(allowedKeys));
        meters.All.Where(m => m.Instrument.StartsWith("internal_commands.lane.", StringComparison.Ordinal))
            .Should().AllSatisfy(m => m.Tags.Values.Should().NotContain("x", "no instrument carries a receiver key"));
    }

    // L1.16: a charged row records what the unchanged failure path records (one failed attempt,
    // reason result_failure) plus one short-circuit, and no lag, duration or processed count.
    [Fact]
    public async Task Cycle_AChargedRow_RecordsAFailedAttemptAndAShortCircuit_ButNoLagOrDuration()
    {
        var lane = CreateLane();
        await OpenCooldownAsync(lane, "x");
        Seed("x", "charged", Now);
        using var meters = new InternalCommandMeterRecorder();

        await lane.Lane.ProcessDueCommandsAsync(Ct);

        meters.For("internal_commands.failed.count").Where(IsProbeRow)
            .Should().ContainSingle()
            .Which.Tag("reason").Should().Be("result_failure");
        meters.For("internal_commands.execution.lag").Where(IsProbeRow).Should().BeEmpty("nothing ran");
        meters.For("internal_commands.execution.duration").Where(IsProbeRow).Should().BeEmpty("nothing ran");
        meters.For("internal_commands.processed.count").Where(IsProbeRow).Should().BeEmpty();
        meters.For("internal_commands.dead_letter.count").Where(IsProbeRow).Should().BeEmpty("one attempt of five");
        meters.For("internal_commands.lane.short_circuited.count").Where(IsProbeLane)
            .Should().ContainSingle().Which.Value.Should().Be(1);
        meters.For("internal_commands.lane.receiver_cooldowns.count").Where(IsProbeLane)
            .Should().BeEmpty("a charged attempt never opens a cooldown");
    }

    // L1.17, L1.9: opening a cooldown logs one Warning carrying the lane, the receiver key and the
    // cooldown seconds; no lane line and no line of the failure path carries the payload or the tenant.
    [Fact]
    public async Task Cycle_OpeningACooldown_LogsOneWarningWithIdsOnly()
    {
        const string payloadMarker = "payload-marker-never-logged";
        const string tenantMarker = "tenant-marker-never-logged";
        var logger = new StructuredRecordingLogger<InternalCommandLaneProcessor>();
        var rowsLogger = new RecordingLogger<InternalCommandProcessor>();
        var lane = CreateLane(logger: logger, rowsLogger: rowsLogger);
        _probes.Fail("x");
        var origin = new InternalCommandOrigin(null, null, null, tenantMarker, null, null, null);
        SeedWithOrigin(new LaneProbeCommand("x", payloadMarker), Now.AddSeconds(-2), origin);
        SeedWithOrigin(new LaneProbeCommand("x", payloadMarker), Now.AddSeconds(-1), origin);

        await lane.Lane.ProcessDueCommandsAsync(Ct);

        var warning = logger.Entries.Where(e => e.Level == LogLevel.Warning).Should().ContainSingle(
            "one Warning per cooldown opening; the charged row behind it opens nothing").Subject;
        warning.CarriesText(LaneName).Should().BeTrue("the line names the lane");
        warning.CarriesText("x").Should().BeTrue("the line names the receiver key");
        warning.CarriesNumber(60).Should().BeTrue("the line carries the cooldown seconds");
        logger.Entries.Should().NotContain(e => e.Mentions(payloadMarker) || e.Mentions(tenantMarker));
        rowsLogger.Entries.Should().NotContain(e => e.Message.Contains(payloadMarker, StringComparison.Ordinal)
            || e.Message.Contains(tenantMarker, StringComparison.Ordinal));
        rowsLogger.Entries.Should().Contain(e => e.EventName == "LogAttemptFailed"
            && e.Message.Contains(CoolingDownError, StringComparison.Ordinal));
    }

    // L1.19: the effective cooldown is min(configured, S), S = sum over k = 1 .. MaxAttempts-1 of
    // min(0.8 x base x 2^(k-1), cap): the shortest span from a row's first attempt to its last.
    [Theory]
    [InlineData(60, 5, 10, 600, 60d)]
    [InlineData(120, 5, 10, 600, 120d)]
    [InlineData(60, 3, 10, 600, 24d)]
    [InlineData(60, 1, 10, 600, 0d)]
    [InlineData(60, 5, 10, 15, 53d)]
    [InlineData(5, 3, 10, 600, 5d)]
    public void EffectiveCooldown_IsClampedToTheShortestRetrySpan(
        int configuredSeconds,
        int maxAttempts,
        int retryBackoffBaseSeconds,
        int maxRetryBackoffSeconds,
        double expectedSeconds)
    {
        var settings = InternalCommandTestHarness.Settings(
            maxAttempts: maxAttempts,
            retryBackoffBaseSeconds: retryBackoffBaseSeconds,
            maxRetryBackoffSeconds: maxRetryBackoffSeconds);

        var effective = InternalCommandLaneProcessor.EffectiveCooldown(TimeSpan.FromSeconds(configuredSeconds), settings);

        effective.TotalSeconds.Should().BeApproximately(expectedSeconds, 0.001);
    }

    // L1.19, L1.17: a clamped cooldown logs exactly one Warning at start naming both numbers, beside
    // the one Information start line carrying the effective value; an unclamped one logs no Warning.
    // DESIGN-QUESTION: "at start" is read as "before the polling loop's startup delay": the fake clock
    // never leaves that delay here, so a start line written after it would never arrive.
    [Fact]
    public async Task Start_WithAClampedCooldown_LogsOneWarning()
    {
        // MaxAttempts 3 at base 10 makes S = 8 + 16 = 24 s, below the configured 60 s.
        var clampedLogger = new StructuredRecordingLogger<InternalCommandLaneProcessor>();
        using var clampedSignal = new InternalCommandSignal();
        var clamped = CreateLane(maxAttempts: 3, logger: clampedLogger, signal: clampedSignal);

        await clamped.Lane.StartAsync(Ct);
        await clampedLogger.WhenCountAsync(2).WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, Ct);
        await clamped.Lane.StopAsync(Ct);

        var warning = clampedLogger.Entries.Where(e => e.Level == LogLevel.Warning).Should().ContainSingle().Subject;
        warning.CarriesNumber(60).Should().BeTrue("the warning names the configured cooldown");
        warning.CarriesNumber(24).Should().BeTrue("the warning names the shortest retry span it was clamped to");
        clampedLogger.Entries.Where(e => e.Level == LogLevel.Information).Should().ContainSingle()
            .Which.CarriesNumber(24).Should().BeTrue("the start line carries the effective cooldown");

        var plainLogger = new StructuredRecordingLogger<InternalCommandLaneProcessor>();
        using var plainSignal = new InternalCommandSignal();
        var plain = CreateLane(maxAttempts: 5, logger: plainLogger, signal: plainSignal);

        await plain.Lane.StartAsync(Ct);
        await plainLogger.WhenCountAsync(1).WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, Ct);
        await plain.Lane.StopAsync(Ct);

        plainLogger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning, "at the defaults nothing is clamped");
    }

    // L1.22, M2's scenario: 50 rows to a black-hole receiver X (each attempt takes 10 s on the fake
    // clock, then fails) ahead of 5 rows to Y. X costs one real attempt; its other 49 rows are charged
    // in the same cycle; Y's rows complete in the next consecutive cycle; no X row is sent again until
    // the clock passes the cooldown that opened at the end of that attempt.
    [Fact]
    public async Task Cycle_ABlackHoleReceiverWithAFullBatch_CostsOneRealAttemptPerCooldown()
    {
        var lane = CreateLane(batchSize: 50);
        _probes.Behave("x", (_, _) =>
        {
            _clock.Advance(TimeSpan.FromSeconds(10));
            return Task.FromResult(LaneProbeLog.FailedDelivery());
        });
        var start = Now;
        for (var index = 0; index < 50; index++)
        {
            Seed("x", $"x-{index:D2}", start.AddSeconds(-200 + index));
        }

        var yRows = Enumerable.Range(0, 5).Select(index => Seed("y", $"y-{index}", start.AddSeconds(-100 + index))).ToList();

        var first = await lane.Lane.ProcessDueCommandsAsync(Ct);

        _probes.ValuesFor("x").Should().Equal(["x-00"], "one real attempt for the whole black-hole batch");
        first.HasMoreDueWork.Should().BeTrue("the batch was full and 49 rows were charged");
        _probes.ValuesFor("y").Should().BeEmpty("Y's rows were outside the first window");
        var opened = Now;

        await lane.Lane.ProcessDueCommandsAsync(Ct);

        _probes.ValuesFor("y").Should().HaveCount(5, "Y completes in the next consecutive cycle");
        foreach (var row in yRows)
        {
            (await ReloadAsync(row.Id)).ProcessedOn.Should().NotBeNull();
        }

        _clock.Advance(opened.AddSeconds(20) - Now);
        await lane.Lane.ProcessDueCommandsAsync(Ct);
        _probes.ValuesFor("x").Should().HaveCount(1, "inside the cooldown every due X row is charged, none sent");

        _clock.Advance(opened.AddSeconds(60) - Now);
        await lane.Lane.ProcessDueCommandsAsync(Ct);
        _probes.ValuesFor("x").Should().HaveCount(2, "after the cooldown exactly one X row is sent: the trial");
    }

    // L1.8 (ReceiverCooldowns surface): Open reports a closed-to-open transition once; a key cools
    // while utcNow < until; Close ends it; Prune drops entries that ended more than one cooldown ago.
    [Fact]
    public void ReceiverCooldowns_OpenReportsTheTransitionOnce_PruneDropsEndedEntries()
    {
        var cooldowns = new ReceiverCooldowns(TimeSpan.FromSeconds(60));
        var t0 = InternalCommandTestHarness.EpochUtc;

        cooldowns.Open("a", t0).Should().BeTrue("closed to open");
        cooldowns.Open("a", t0.AddSeconds(1)).Should().BeFalse("already open: no second transition");
        cooldowns.IsCoolingDown("a", t0.AddSeconds(59)).Should().BeTrue();
        cooldowns.IsCoolingDown("b", t0).Should().BeFalse("a key never opened is not cooling");

        cooldowns.Close("a");
        cooldowns.IsCoolingDown("a", t0.AddSeconds(2)).Should().BeFalse("Close ends the cooldown");
        cooldowns.Open("a", t0.AddSeconds(2)).Should().BeTrue("a closed key opens again");

        cooldowns.Open("b", t0).Should().BeTrue();
        cooldowns.IsCoolingDown("b", t0.AddSeconds(60)).Should().BeFalse("cooling only while utcNow < until");
        cooldowns.Open("b", t0.AddSeconds(60)).Should().BeTrue("an ended cooldown reopens as a new transition");

        var c = new ReceiverCooldowns(TimeSpan.FromSeconds(60));
        c.Open("k", t0);
        c.Prune(t0.AddSeconds(120));
        c.Count.Should().Be(1, "it ended exactly one cooldown ago, not more");
        c.Prune(t0.AddSeconds(120.001));
        c.Count.Should().Be(0, "it ended more than one cooldown ago");
    }

    private static bool IsProbeLane(RecordedMeasurement measurement) =>
        measurement.Tag("lane") == LaneName;

    private static bool IsProbeRow(RecordedMeasurement measurement) =>
        measurement.Tag("command_type") == ProbeCommandType;

    private LaneUnderTest CreateLane(
        int maxAttempts = 5,
        int batchSize = 50,
        InternalCommandLane? lane = null,
        ILogger<InternalCommandLaneProcessor>? logger = null,
        ILogger<InternalCommandProcessor>? rowsLogger = null,
        IInternalCommandSignal? signal = null,
        TenancySettings? tenancy = null,
        bool registerHandler = true)
    {
        _settings = InternalCommandTestHarness.Settings(maxAttempts: maxAttempts, batchSize: batchSize);
        var created = InternalCommandLaneTestHarness.CreateLaneProcessor(
            _context,
            _settings,
            _clock,
            _probes,
            lane,
            services => InternalCommandTestHarness.AddRecordingCommandPipeline(services, _shared),
            logger,
            rowsLogger,
            signal,
            tenancy,
            registerHandler);
        _disposables.Add(created);
        return created;
    }

    /// <summary>
    /// Opens <paramref name="receiver"/>'s cooldown at the current instant with one real failed attempt:
    /// a row on its last attempt fails and dead-letters (L1.8: a failed real attempt opens the cooldown
    /// whether the row then retries or dead-letters), leaving nothing behind to come due again. The
    /// receiver succeeds afterwards; a test that wants it failing sets that itself.
    /// </summary>
    private async Task OpenCooldownAsync(LaneUnderTest lane, string receiver)
    {
        var poison = Seed(receiver, $"poison-{receiver}", Now.AddHours(-1), attempts: _settings.MaxAttempts - 1);
        _probes.Fail(receiver);

        await lane.Lane.ProcessDueCommandsAsync(Ct);

        _probes.Succeed(receiver);
        (await ReloadAsync(poison.Id)).DeadLetteredOn.Should().NotBeNull(
            "setup: the poison row's failed real attempt is what opens the cooldown");
    }

    private InternalCommandMessage Seed(
        string receiver,
        string value,
        DateTime scheduledOn,
        int attempts = 0,
        DateTime? claimedUntil = null,
        Guid? claimedBy = null) =>
        SeedCommand(new LaneProbeCommand(receiver, value), scheduledOn, attempts, claimedUntil, claimedBy);

    private InternalCommandMessage SeedCommand(
        IInternalCommand command,
        DateTime scheduledOn,
        int attempts = 0,
        DateTime? claimedUntil = null,
        Guid? claimedBy = null)
    {
        var row = InternalCommandTestHarness.Seed(_context, command, scheduledOn, attempts, claimedUntil, claimedBy);
        _context.ChangeTracker.Clear();
        return row;
    }

    private InternalCommandMessage SeedWithOrigin(IInternalCommand command, DateTime scheduledOn, InternalCommandOrigin origin)
    {
        var row = InternalCommandMessage.FromCommand(command, scheduledOn, scheduledOn, origin);
        _context.Add(row);
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
        return row;
    }

    private Task<InternalCommandMessage> ReloadAsync(Guid id)
    {
        _context.ChangeTracker.Clear();
        return _context.Set<InternalCommandMessage>().AsNoTracking()
            .SingleAsync(c => c.Id == id, Ct);
    }
}

using System.Globalization;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;

namespace MMCA.Common.Infrastructure.Tests.Persistence.InternalCommands.Processing;

/// <summary>
/// Two lane instances (two replicas) over ONE database, for the amended recovery guarantee (ruling F1 on
/// the Phase 1 review, amending L1.10 and L1.19): the effective cooldown is
/// <c>min(ReceiverCooldownSeconds, S - TimeoutSeconds - 1)</c>, floored at 1 second, so a cooldown that
/// opens AFTER a row was scheduled, from a POST that started before the receiver recovered and failed up
/// to <c>TimeoutSeconds</c> later, still ends before the row's last attempt.
/// <para>
/// Runs on <see cref="InternalCommandLaneDatabase"/> (a context per scope over a temp-file database),
/// because the two instances and the in-flight POST run at the same time.
/// </para>
/// </summary>
public sealed class InternalCommandLaneReplicaTests : IDisposable
{
    private static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(30);

    private readonly List<InternalCommandLaneDatabase> _databases = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var database in _databases)
        {
            database.Dispose();
        }
    }

    // F1, the reviewer's schedule (results/review-lane-phase1.md F1), shrunk to MaxAttempts 3 so the clamp
    // engages: S = 0.8 x (10 + 20) = 24 s; TimeoutSeconds (the lane's BatchStartBudget) = 10 s; configured
    // cooldown 60 s; effective cooldown min(60, 24 - 10 - 1) = 13 s. Under the Phase 1 clamp (min(60, 24) =
    // 24 s) the same schedule dead-letters the row in almost every jitter draw.
    //   T0     A's real attempt of receiver K fails: A cools K until T0+13.
    //   T0     B starts a POST to K (row P) that will fail 10 s later; K recovers right after it went out.
    //   T0+1   row R for K is scheduled; K answers successfully from now on.
    //   T0+1   A reaches R while K cools on A: attempt 1 charged; R is due again at T0+9 to T0+13.
    //   T0+10  B's POST fails: B cools K until T0+23, a cooldown opened AFTER R was scheduled.
    //   ~T0+10 B reaches R (every later attempt is driven through B, the worst case): attempt 2 charged,
    //          since T0+13 at the latest is inside B's window; R is due again 16 to 24 s later.
    //   >=T0+26 attempt 3, R's LAST, is past T0+23 on any jitter: B sends it for real and it completes.
    // Ten independent trials, each on a fresh database, sample the jitter.
    [Fact]
    public async Task TwoLaneInstances_ACooldownOpenedAfterARowWasScheduled_StillLeavesTheRowARealAttempt()
    {
        for (var trial = 0; trial < 10; trial++)
        {
            await RunTrialAsync(trial);
        }
    }

    private async Task RunTrialAsync(int trial)
    {
        var clock = new FakeTimeProvider(InternalCommandTestHarness.Epoch);
        var db = new InternalCommandLaneDatabase(clock);
        _databases.Add(db);
        var settings = InternalCommandTestHarness.Settings(maxAttempts: 3, retryBackoffBaseSeconds: 10, maxRetryBackoffSeconds: 600);
        var timeout = TimeSpan.FromSeconds(10);
        var replicaA = db.CreateLane(settings, InternalCommandLaneDatabase.Lane(1, timeout, TimeSpan.FromSeconds(60), name: "probe-replica"));
        var replicaB = db.CreateLane(settings, InternalCommandLaneDatabase.Lane(1, timeout, TimeSpan.FromSeconds(60), name: "probe-replica"));
        var t0 = clock.GetUtcNow().UtcDateTime;
        var label = string.Create(CultureInfo.InvariantCulture, $"trial {trial}");

        // T0: A's real attempt fails and cools K on A.
        await db.OpenCooldownAsync(replicaA, "k", settings.MaxAttempts, Ct);

        // T0: B starts the POST of P to K, which fails 10 s later (held until the test releases it).
        db.Seed("k", "in-flight", t0.AddHours(-1), attempts: settings.MaxAttempts - 1);
        var inFlight = db.Probes.Hold("in-flight");
        var bCycle = Task.Run(() => replicaB.Lane.ProcessDueCommandsAsync(Ct), Ct);
        await db.Probes.WhenStartedCountAsync(2).WaitAsync(SafetyTimeout, TimeProvider.System, Ct);

        // T0+1: R is scheduled; K answers successfully from now on (R has no behaviour: success).
        clock.Advance(TimeSpan.FromSeconds(1));
        var row = db.Seed("k", "scheduled-after-recovery", clock.GetUtcNow().UtcDateTime);

        // T0+1: A charges attempt 1 (K is cooling on A).
        await replicaA.Lane.ProcessDueCommandsAsync(Ct);
        var afterA = await db.ReloadAsync(row.Id, Ct);
        afterA.Attempts.Should().Be(1, $"{label}: setup, A charges R's first attempt");
        afterA.LastError.Should().Be(InternalCommandLaneDatabase.CoolingDownError, label);

        // T0+10: B's POST fails, so B's cooldown on K opens after R was scheduled.
        clock.Advance(t0.AddSeconds(10) - clock.GetUtcNow().UtcDateTime);
        inFlight.SetResult(ConcurrentProbeLog.FailedDelivery());
        await bCycle.WaitAsync(SafetyTimeout, TimeProvider.System, Ct);

        // Every later attempt of R goes through B.
        for (var step = 0; step < 10; step++)
        {
            var current = await db.ReloadAsync(row.Id, Ct);
            if (current.ProcessedOn is not null || current.DeadLetteredOn is not null)
            {
                break;
            }

            current.ClaimedUntil.Should().NotBeNull($"{label}: an unfinished row is parked for its backoff");
            var wait = current.ClaimedUntil.Value - clock.GetUtcNow().UtcDateTime;
            if (wait > TimeSpan.Zero)
            {
                clock.Advance(wait);
            }

            // One tick past the parked time: the pending predicate is strictly ClaimedUntil < now.
            clock.Advance(TimeSpan.FromTicks(1));
            await replicaB.Lane.ProcessDueCommandsAsync(Ct);
        }

        var stored = await db.ReloadAsync(row.Id, Ct);
        stored.DeadLetteredOn.Should().BeNull($"{label}: a receiver healthy from scheduling gets a real attempt before R dead-letters");
        stored.ProcessedOn.Should().NotBeNull(label);
        stored.Attempts.Should().Be(3, $"{label}: attempt 1 charged by A, attempt 2 charged by B inside its late cooldown, attempt 3 real");
        db.Probes.ValuesFor("k").Should().Equal(["poison-k", "in-flight", "scheduled-after-recovery"], label);
    }
}

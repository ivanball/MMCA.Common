using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Http;
using MMCA.Common.UI.Services.Api;
using MMCA.Common.UI.Services.Offline;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Offline;

/// <summary>
/// Pins <see cref="OfflineWriteQueue"/> (item 38, client side): the durable offline write queue.
/// One list under one store key, per-user scoping, enqueue stamping, cancel by coalesce key, and the
/// replay pass with its classification of each handler result into kept (transient), Completed,
/// Conflict, Rejected or Expired. Runs over an in-memory JSON store, a connectivity double the test
/// flips, a switchable signed-in user and a <see cref="FakeTimeProvider"/>.
/// </summary>
public sealed class OfflineWriteQueueTests : IDisposable
{
    private const string WidgetKind = "Tests.Widget.Create.v1";
    private const string OtherKind = "Tests.Widget.Delete.v1";
    private const string UnknownKind = "Tests.Gadget.Create.v1";

    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(5);

    private readonly InMemoryLocalCacheStore _store = new();
    private readonly ControllableConnectivityService _connectivity = new();
    private readonly SwitchableAuthenticationStateProvider _auth = SwitchableAuthenticationStateProvider.SignedInAs("7");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero));
    private readonly OfflineWriteQueueOptions _options = new();
    private readonly Mock<ILogger<OfflineWriteQueue>> _logger = new();
    private readonly List<OfflineWriteQueue> _queues = [];

    public OfflineWriteQueueTests() =>
        _logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

    public sealed record WidgetPayload(int WidgetId, string Note);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var queue in _queues)
        {
            queue.Dispose();
        }
    }

    // Options
    [Fact]
    public void Options_Defaults()
    {
        var options = new OfflineWriteQueueOptions();

        options.StorageKey.Should().Be("mmca.offline-writes");
        options.MaxAge.Should().Be(TimeSpan.FromHours(24), "the server keeps idempotency replays for 24 hours");
        options.MaxPendingWrites.Should().Be(200);
    }

    // Rule 1: availability
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsAvailable_MirrorsTheStore(bool available)
    {
        _store.IsAvailable = available;

        CreateQueue().IsAvailable.Should().Be(available);
    }

    // Rule 2: one list under one key, serialized mutations
    [Fact]
    public async Task Enqueue_PersistsOneListUnderTheStorageKey()
    {
        _options.StorageKey = "tests.offline-queue";
        var queue = CreateQueue();

        await queue.EnqueueAsync(WidgetKind, new WidgetPayload(1, "a"), cancellationToken: Ct);
        await queue.EnqueueAsync(WidgetKind, new WidgetPayload(2, "b"), cancellationToken: Ct);

        _store.Keys.Should().Equal("tests.offline-queue");
        _store.Peek<List<PendingWrite>>("tests.offline-queue").Should().HaveCount(2);
    }

    [Fact]
    public async Task Enqueue_Concurrent_NoWriteIsLost()
    {
        var queue = CreateQueue();

        await Task.WhenAll(Enumerable.Range(1, 25)
            .Select(i => queue.EnqueueAsync(WidgetKind, new WidgetPayload(i, "x"), cancellationToken: Ct)));

        Stored().Should().HaveCount(25, "every read-edit-write runs under one semaphore, so no enqueue overwrites another");
        (await queue.GetPendingAsync(Ct)).Should().HaveCount(25);
    }

    // Rule 3: soft failures
    [Fact]
    public async Task Enqueue_Unavailable_ReturnsUnavailableFailure()
    {
        _store.IsAvailable = false;
        var queue = CreateQueue();

        var result = await queue.EnqueueAsync(WidgetKind, new WidgetPayload(1, "a"), cancellationToken: Ct);

        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("OfflineWrites.Unavailable");
        error.Type.Should().Be(ErrorType.Failure);
        _store.WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task Enqueue_Full_ReturnsQueueFullFailure()
    {
        _options.MaxPendingWrites = 2;
        var queue = CreateQueue();
        (await queue.EnqueueAsync(WidgetKind, new WidgetPayload(1, "a"), cancellationToken: Ct)).IsSuccess.Should().BeTrue();
        (await queue.EnqueueAsync(WidgetKind, new WidgetPayload(2, "b"), cancellationToken: Ct)).IsSuccess.Should().BeTrue();

        var result = await queue.EnqueueAsync(WidgetKind, new WidgetPayload(3, "c"), cancellationToken: Ct);

        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("OfflineWrites.QueueFull");
        error.Type.Should().Be(ErrorType.Failure);
        Stored().Should().HaveCount(2);
    }

    // Rule 4: stamping
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Enqueue_BlankKind_Throws(string kind)
    {
        var queue = CreateQueue();

        var act = () => queue.EnqueueAsync(kind, new WidgetPayload(1, "a"), cancellationToken: Ct);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Enqueue_StampsIdKeyScopeTimeAndPayload()
    {
        var queue = CreateQueue();
        var payload = new WidgetPayload(5, "Front row");

        var result = await queue.EnqueueAsync(WidgetKind, payload, cancellationToken: Ct);
        var other = await queue.EnqueueAsync(WidgetKind, payload, cancellationToken: Ct);

        result.IsSuccess.Should().BeTrue();
        var write = result.Value!;
        write.Id.Should().NotBe(Guid.Empty);
        write.Kind.Should().Be(WidgetKind);
        Guid.TryParseExact(write.IdempotencyKey, "N", out _).Should().BeTrue("a generated key is a Guid in \"N\" form");
        write.PayloadJson.Should().Be(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web));
        write.UserScope.Should().Be("7", "the signed-in subject's id, invariant string");
        write.EnqueuedOn.Should().Be(_time.GetUtcNow());
        write.Attempts.Should().Be(0);
        write.BasedOnRowVersion.Should().BeNull();
        write.CoalesceKey.Should().BeNull();

        other.Value!.Id.Should().NotBe(write.Id);
        other.Value.IdempotencyKey.Should().NotBe(write.IdempotencyKey);

        var stored = Stored().Single(w => w.Id == write.Id);
        stored.IdempotencyKey.Should().Be(write.IdempotencyKey);
        stored.PayloadJson.Should().Be(write.PayloadJson);
    }

    [Fact]
    public async Task Enqueue_UsesSuppliedIdempotencyKeyAndRowVersion()
    {
        var queue = CreateQueue();
        byte[] rowVersion = [0, 0, 0, 0, 0, 0, 7, 209];

        var result = await queue.EnqueueAsync(
            WidgetKind,
            new WidgetPayload(5, "Front row"),
            new OfflineWriteOptions { IdempotencyKey = "client-key-1", BasedOnRowVersion = rowVersion, CoalesceKey = "widget:7:5" },
            Ct);

        var write = result.Value!;
        write.IdempotencyKey.Should().Be("client-key-1");
        write.BasedOnRowVersion.Should().Equal(rowVersion);
        write.CoalesceKey.Should().Be("widget:7:5");

        var stored = Stored().Should().ContainSingle().Subject;
        stored.IdempotencyKey.Should().Be("client-key-1");
        stored.BasedOnRowVersion.Should().Equal(rowVersion);
        stored.CoalesceKey.Should().Be("widget:7:5");
    }

    [Fact]
    public async Task Enqueue_Anonymous_HasNoUserScope()
    {
        _auth.SignOut();
        var queue = CreateQueue();

        var result = await queue.EnqueueAsync(WidgetKind, new WidgetPayload(1, "a"), cancellationToken: Ct);

        result.Value!.UserScope.Should().BeNull();
    }

    // Rule 5: PendingChanged
    [Fact]
    public async Task Enqueue_RaisesPendingChanged()
    {
        var queue = CreateQueue();
        var storedCountAtEvent = new List<int>();
        queue.PendingChanged += (_, _) => storedCountAtEvent.Add(Stored().Count);

        await queue.EnqueueAsync(WidgetKind, new WidgetPayload(1, "a"), cancellationToken: Ct);

        storedCountAtEvent.Should().NotBeEmpty();
        storedCountAtEvent.Should().AllSatisfy(count => count.Should().Be(1, "the event follows the persisted change"));
    }

    [Fact]
    public async Task PendingChanged_RaisedOnCancelAttemptAndSettle()
    {
        var outcome = Result.Failure(Error.Failure(HttpResultExecutor.TransportErrorCode, "No route."));
        var handler = new ScriptedWriteHandler(WidgetKind, (_, _) => Task.FromResult(outcome));
        Seed(Write(OtherKind, "7", TimeSpan.FromMinutes(20), coalesceKey: "widget:7:9"), Write(WidgetKind, "7", TimeSpan.FromMinutes(10)));
        var queue = CreateQueue(handler);
        var raised = 0;
        queue.PendingChanged += (_, _) => Interlocked.Increment(ref raised);

        (await queue.CancelPendingAsync("widget:7:9", Ct)).Should().BeTrue();
        Volatile.Read(ref raised).Should().BeGreaterThan(0, "a cancel is a persisted change");

        Interlocked.Exchange(ref raised, 0);
        await queue.ReplayAsync(Ct);
        Stored().Should().ContainSingle().Which.Attempts.Should().Be(1);
        Volatile.Read(ref raised).Should().BeGreaterThan(0, "an attempt count change is a persisted change");

        Interlocked.Exchange(ref raised, 0);
        outcome = Result.Success();
        await queue.ReplayAsync(Ct);
        Stored().Should().BeEmpty();
        Volatile.Read(ref raised).Should().BeGreaterThan(0, "a settle is a persisted change");
    }

    // Rule 6: pending view
    [Fact]
    public async Task Pending_PersistAcrossQueueInstances()
    {
        var first = CreateQueue();
        var a = (await first.EnqueueAsync(WidgetKind, new WidgetPayload(1, "a"), cancellationToken: Ct)).Value!;
        _time.Advance(TimeSpan.FromSeconds(1));
        var b = (await first.EnqueueAsync(WidgetKind, new WidgetPayload(2, "b"), cancellationToken: Ct)).Value!;

        var second = CreateQueue();
        var pending = await second.GetPendingAsync(Ct);

        pending.Select(w => w.Id).Should().Equal(a.Id, b.Id);
    }

    [Fact]
    public async Task GetPending_HidesOtherScopes_OrdersOldestFirst()
    {
        var newest = Write(WidgetKind, "7", TimeSpan.FromMinutes(1));
        var oldest = Write(WidgetKind, "7", TimeSpan.FromMinutes(30));
        var middle = Write(WidgetKind, "7", TimeSpan.FromMinutes(10));
        var foreign = Write(WidgetKind, "8", TimeSpan.FromMinutes(20));
        Seed(newest, foreign, oldest, middle);
        var queue = CreateQueue();

        var pending = await queue.GetPendingAsync(Ct);

        pending.Select(w => w.Id).Should().Equal(oldest.Id, middle.Id, newest.Id);

        _auth.SignInAs("8");
        (await queue.GetPendingAsync(Ct)).Select(w => w.Id).Should().Equal(foreign.Id);
    }

    // Rule 7: cancel by coalesce key
    [Fact]
    public async Task CancelPending_RemovesMatchingKey_ReturnsTrue_NoSettledEvent()
    {
        var match1 = Write(WidgetKind, "7", TimeSpan.FromMinutes(3), coalesceKey: "widget:7:5");
        var match2 = Write(OtherKind, "7", TimeSpan.FromMinutes(2), coalesceKey: "widget:7:5");
        var differentCase = Write(WidgetKind, "7", TimeSpan.FromMinutes(4), coalesceKey: "Widget:7:5");
        var otherKey = Write(WidgetKind, "7", TimeSpan.FromMinutes(5), coalesceKey: "widget:7:6");
        var foreignScope = Write(WidgetKind, "8", TimeSpan.FromMinutes(6), coalesceKey: "widget:7:5");
        Seed(match1, match2, differentCase, otherKey, foreignScope);
        var queue = CreateQueue();
        var settled = new List<OfflineWriteSettledEventArgs>();
        queue.WriteSettled += (_, args) => settled.Add(args);

        var removed = await queue.CancelPendingAsync("widget:7:5", Ct);

        removed.Should().BeTrue();
        Guid[] survivors = [differentCase.Id, otherKey.Id, foreignScope.Id];
        Stored().Select(w => w.Id).Should().BeEquivalentTo(
            survivors,
            "the key matches ordinally and only within the current user's scope");
        settled.Should().BeEmpty("a cancel is not a settle");
    }

    [Fact]
    public async Task CancelPending_NoMatch_ReturnsFalse()
    {
        Seed(Write(WidgetKind, "7", coalesceKey: "widget:7:6"));
        var queue = CreateQueue();

        var removed = await queue.CancelPendingAsync("widget:7:5", Ct);

        removed.Should().BeFalse();
        Stored().Should().ContainSingle();
    }

    // Rule 8: replay preconditions
    [Fact]
    public async Task Replay_Offline_ReturnsZero_HandlerNotCalled()
    {
        _connectivity.SetOnline(false);
        var handler = ScriptedWriteHandler.Returning(WidgetKind, Result.Success());
        Seed(Write(WidgetKind, "7"));
        var queue = CreateQueue(handler);
        var reads = _store.ReadCount;
        var writes = _store.WriteCount;

        var settled = await queue.ReplayAsync(Ct);

        settled.Should().Be(0);
        handler.Calls.Should().BeEmpty();
        _store.ReadCount.Should().Be(reads, "an offline replay does not touch the store");
        _store.WriteCount.Should().Be(writes);
        Stored().Should().ContainSingle();
    }

    [Fact]
    public async Task Replay_Unavailable_ReturnsZero_StoreUntouched()
    {
        _store.IsAvailable = false;
        var handler = ScriptedWriteHandler.Returning(WidgetKind, Result.Success());
        var queue = CreateQueue(handler);

        var settled = await queue.ReplayAsync(Ct);

        settled.Should().Be(0);
        handler.Calls.Should().BeEmpty();
        _store.ReadCount.Should().Be(0);
        _store.WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task Replay_Concurrent_SecondCallReturnsZero()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedWriteHandler(WidgetKind, (_, _) =>
        {
            entered.TrySetResult();
            return gate.Task;
        });
        Seed(Write(WidgetKind, "7"));
        var queue = CreateQueue(handler);

        var first = queue.ReplayAsync(Ct);
        await entered.Task.WaitAsync(WaitBudget, TimeProvider.System, Ct);
        var reads = _store.ReadCount;

        var second = await queue.ReplayAsync(Ct);

        second.Should().Be(0, "replay is single flight");
        _store.ReadCount.Should().Be(reads, "the refused replay does not touch the store");
        gate.SetResult(Result.Success());
        (await first.WaitAsync(WaitBudget, TimeProvider.System, Ct)).Should().Be(1);
        handler.Calls.Should().ContainSingle();
    }

    // Rule 9: order and scope
    [Fact]
    public async Task Replay_WalksTheCurrentScopeOldestFirst()
    {
        var newest = Write(WidgetKind, "7", TimeSpan.FromMinutes(1));
        var oldest = Write(WidgetKind, "7", TimeSpan.FromMinutes(30));
        var middle = Write(WidgetKind, "7", TimeSpan.FromMinutes(10));
        Seed(newest, oldest, middle);
        var handler = ScriptedWriteHandler.Returning(WidgetKind, Result.Success());
        var queue = CreateQueue(handler);

        var settled = await queue.ReplayAsync(Ct);

        settled.Should().Be(3);
        handler.Calls.Select(w => w.Id).Should().Equal(oldest.Id, middle.Id, newest.Id);
    }

    [Fact]
    public async Task Replay_OtherScopeEntries_Untouched()
    {
        var own = Write(WidgetKind, "7", TimeSpan.FromMinutes(5));
        var foreign = Write(WidgetKind, "8", TimeSpan.FromMinutes(10));
        Seed(own, foreign);
        var handler = ScriptedWriteHandler.Returning(WidgetKind, Result.Success());
        var queue = CreateQueue(handler);

        var settled = await queue.ReplayAsync(Ct);

        settled.Should().Be(1);
        handler.Calls.Select(w => w.Id).Should().Equal(own.Id);
        var left = Stored().Should().ContainSingle().Subject;
        left.Id.Should().Be(foreign.Id);
        left.Attempts.Should().Be(0);
    }

    // Rule 10: expiry
    [Fact]
    public async Task Replay_OlderThanMaxAge_RaisesExpired_HandlerNotCalled()
    {
        var stale = Write(WidgetKind, "7", TimeSpan.FromHours(25));
        Seed(stale);
        var handler = ScriptedWriteHandler.Returning(WidgetKind, Result.Success());
        var queue = CreateQueue(handler);
        var settled = new List<OfflineWriteSettledEventArgs>();
        queue.WriteSettled += (_, args) => settled.Add(args);

        var count = await queue.ReplayAsync(Ct);

        count.Should().Be(1);
        handler.Calls.Should().BeEmpty("a write past the server's replay window is never sent");
        var args = settled.Should().ContainSingle().Subject;
        args.Outcome.Should().Be(OfflineWriteOutcome.Expired);
        args.Result.Should().BeNull();
        args.Write.Id.Should().Be(stale.Id);
        Stored().Should().BeEmpty();
    }

    [Fact]
    public async Task Replay_MaxAgeOption_IsHonored()
    {
        _options.MaxAge = TimeSpan.FromHours(1);
        var stale = Write(WidgetKind, "7", TimeSpan.FromHours(2));
        var fresh = Write(WidgetKind, "7", TimeSpan.FromMinutes(30));
        Seed(stale, fresh);
        var handler = ScriptedWriteHandler.Returning(WidgetKind, Result.Success());
        var queue = CreateQueue(handler);

        await queue.ReplayAsync(Ct);

        handler.Calls.Select(w => w.Id).Should().Equal(fresh.Id);
        Stored().Should().BeEmpty();
    }

    // Rule 11: unknown kind
    [Fact]
    public async Task Replay_UnknownKind_SkipsAndKeeps()
    {
        // DESIGN-QUESTION: rule 11 says "skipped and kept" without saying whether the pass goes on; tested reading: the pass continues to the next entry (only rule 12's transient branch stops it).
        var unknown = Write(UnknownKind, "7", TimeSpan.FromMinutes(10));
        var known = Write(WidgetKind, "7", TimeSpan.FromMinutes(5));
        Seed(unknown, known);
        var handler = ScriptedWriteHandler.Returning(WidgetKind, Result.Success());
        var queue = CreateQueue(handler);
        var settled = new List<OfflineWriteSettledEventArgs>();
        queue.WriteSettled += (_, args) => settled.Add(args);

        var count = await queue.ReplayAsync(Ct);

        count.Should().Be(1);
        handler.Calls.Select(w => w.Id).Should().Equal(known.Id);
        var kept = Stored().Should().ContainSingle().Subject;
        kept.Id.Should().Be(unknown.Id);
        kept.Attempts.Should().Be(0);
        settled.Select(a => a.Write.Id).Should().Equal(known.Id);
    }

    [Fact]
    public async Task Replay_HandlerKindMatchIsOrdinal()
    {
        var write = Write("tests.widget.create.v1", "7");
        Seed(write);
        var handler = ScriptedWriteHandler.Returning(WidgetKind, Result.Success());
        var queue = CreateQueue(handler);

        var count = await queue.ReplayAsync(Ct);

        count.Should().Be(0);
        handler.Calls.Should().BeEmpty("the kind lookup is ordinal, so a differently cased kind has no handler");
        Stored().Should().ContainSingle();
    }

    // Rule 12: classification
    [Fact]
    public async Task Replay_Success_RemovesEntry_RaisesCompleted()
    {
        var handler = ScriptedWriteHandler.Returning(WidgetKind, Result.Success());
        var queue = CreateQueue(handler);
        var payload = new WidgetPayload(5, "Front row");
        await queue.EnqueueAsync(WidgetKind, payload, new OfflineWriteOptions { IdempotencyKey = "client-key-1" }, Ct);
        var settled = new List<OfflineWriteSettledEventArgs>();
        queue.WriteSettled += (_, args) => settled.Add(args);

        var count = await queue.ReplayAsync(Ct);

        count.Should().Be(1);
        var replayed = handler.Calls.Should().ContainSingle().Subject;
        replayed.IdempotencyKey.Should().Be("client-key-1", "the key travels from enqueue to replay");
        replayed.ReadPayload<WidgetPayload>().Should().Be(payload);
        var args = settled.Should().ContainSingle().Subject;
        args.Outcome.Should().Be(OfflineWriteOutcome.Completed);
        args.Result!.IsSuccess.Should().BeTrue();
        Stored().Should().BeEmpty();
    }

    [Fact]
    public async Task Replay_TransportFailure_KeepsEntry_IncrementsAttempts_StopsPass()
    {
        var first = Write(WidgetKind, "7", TimeSpan.FromMinutes(10));
        var second = Write(WidgetKind, "7", TimeSpan.FromMinutes(5));
        Seed(first, second);
        var handler = ScriptedWriteHandler.Returning(
            WidgetKind,
            Result.Failure(Error.Failure(HttpResultExecutor.TransportErrorCode, "No route.")));
        var queue = CreateQueue(handler);
        var settled = new List<OfflineWriteSettledEventArgs>();
        queue.WriteSettled += (_, args) => settled.Add(args);

        var count = await queue.ReplayAsync(Ct);

        count.Should().Be(0);
        handler.Calls.Should().ContainSingle("a transient failure stops the pass").Which.Id.Should().Be(first.Id);
        Stored().Single(w => w.Id == first.Id).Attempts.Should().Be(1);
        Stored().Single(w => w.Id == second.Id).Attempts.Should().Be(0);
        settled.Should().BeEmpty();
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("unexpected")]
    [InlineData("too-many-requests")]
    [InlineData("unauthorized")]
    public async Task Replay_OtherTransientErrors_KeepEntry_IncrementAttempts_StopPass(string errorKind)
    {
        var first = Write(WidgetKind, "7", TimeSpan.FromMinutes(10));
        var second = Write(WidgetKind, "7", TimeSpan.FromMinutes(5));
        Seed(first, second);
        var handler = ScriptedWriteHandler.Returning(WidgetKind, Result.Failure(TransientError(errorKind)));
        var queue = CreateQueue(handler);

        var count = await queue.ReplayAsync(Ct);

        count.Should().Be(0);
        handler.Calls.Should().ContainSingle();
        Stored().Single(w => w.Id == first.Id).Attempts.Should().Be(1);
        Stored().Single(w => w.Id == second.Id).Attempts.Should().Be(0);
    }

    [Fact]
    public async Task Replay_Unauthorized_KeepsEntry()
    {
        var write = Write(WidgetKind, "7");
        Seed(write);
        var handler = ScriptedWriteHandler.Returning(WidgetKind, Result.Failure(Error.Unauthorized("Http.401", "Sign in again.")));
        var queue = CreateQueue(handler);
        var settled = new List<OfflineWriteSettledEventArgs>();
        queue.WriteSettled += (_, args) => settled.Add(args);

        var count = await queue.ReplayAsync(Ct);

        count.Should().Be(0);
        var kept = Stored().Should().ContainSingle().Subject;
        kept.Id.Should().Be(write.Id);
        kept.Attempts.Should().Be(1);
        settled.Should().BeEmpty("an expired session is retried after sign-in, not discarded");
    }

    [Fact]
    public async Task Replay_Conflict_RemovesEntry_RaisesConflictWithResult()
    {
        var write = Write(WidgetKind, "7");
        Seed(write);
        var conflict = Result.Failure(Error.Conflict("Concurrency.PreconditionFailed", "The item changed."));
        var handler = ScriptedWriteHandler.Returning(WidgetKind, conflict);
        var queue = CreateQueue(handler);
        var settled = new List<OfflineWriteSettledEventArgs>();
        queue.WriteSettled += (_, args) => settled.Add(args);

        var count = await queue.ReplayAsync(Ct);

        count.Should().Be(1);
        var args = settled.Should().ContainSingle().Subject;
        args.Outcome.Should().Be(OfflineWriteOutcome.Conflict);
        args.Result.Should().BeSameAs(conflict);
        args.Write.Id.Should().Be(write.Id);
        Stored().Should().BeEmpty();
    }

    [Fact]
    public async Task Replay_Validation_RemovesEntry_RaisesRejected()
    {
        var write = Write(WidgetKind, "7");
        Seed(write);
        var refused = Result.Failure(Error.Validation("Widget.Note.Required", "A note is required."));
        var handler = ScriptedWriteHandler.Returning(WidgetKind, refused);
        var queue = CreateQueue(handler);
        var settled = new List<OfflineWriteSettledEventArgs>();
        queue.WriteSettled += (_, args) => settled.Add(args);

        var count = await queue.ReplayAsync(Ct);

        count.Should().Be(1);
        var args = settled.Should().ContainSingle().Subject;
        args.Outcome.Should().Be(OfflineWriteOutcome.Rejected);
        args.Result.Should().BeSameAs(refused);
        Stored().Should().BeEmpty();
    }

    [Theory]
    [InlineData("not-found")]
    [InlineData("forbidden")]
    [InlineData("failure")]
    [InlineData("unprocessable")]
    [InlineData("invariant")]
    public async Task Replay_OtherPermanentFailures_AreRejected(string errorKind)
    {
        Seed(Write(WidgetKind, "7"));
        var handler = ScriptedWriteHandler.Returning(WidgetKind, Result.Failure(PermanentError(errorKind)));
        var queue = CreateQueue(handler);
        var settled = new List<OfflineWriteSettledEventArgs>();
        queue.WriteSettled += (_, args) => settled.Add(args);

        var count = await queue.ReplayAsync(Ct);

        count.Should().Be(1);
        settled.Should().ContainSingle().Which.Outcome.Should().Be(OfflineWriteOutcome.Rejected);
        Stored().Should().BeEmpty();
    }

    [Fact]
    public async Task Replay_TransientAndConflictTogether_IsKeptAsTransient()
    {
        Seed(Write(WidgetKind, "7"));
        var mixed = Result.Failure(
        [
            Error.Conflict("Concurrency.PreconditionFailed", "The item changed."),
            Error.Failure(HttpResultExecutor.TimeoutErrorCode, "Timed out."),
        ]);
        var handler = ScriptedWriteHandler.Returning(WidgetKind, mixed);
        var queue = CreateQueue(handler);

        var count = await queue.ReplayAsync(Ct);

        count.Should().Be(0, "the transient check runs before the conflict check");
        Stored().Should().ContainSingle().Which.Attempts.Should().Be(1);
    }

    // Rule 13: handler exceptions
    [Fact]
    public async Task Replay_HandlerThrows_TreatedAsTransient()
    {
        var first = Write(WidgetKind, "7", TimeSpan.FromMinutes(10));
        var second = Write(WidgetKind, "7", TimeSpan.FromMinutes(5));
        Seed(first, second);
        var boom = new InvalidOperationException("The handler broke.");
        var handler = new ScriptedWriteHandler(WidgetKind, (_, _) => Task.FromException<Result>(boom));
        var queue = CreateQueue(handler);
        var settled = new List<OfflineWriteSettledEventArgs>();
        queue.WriteSettled += (_, args) => settled.Add(args);

        var count = await queue.ReplayAsync(Ct);

        count.Should().Be(0);
        handler.Calls.Should().ContainSingle("the pass stops at the first transient failure");
        Stored().Single(w => w.Id == first.Id).Attempts.Should().Be(1);
        Stored().Single(w => w.Id == second.Id).Attempts.Should().Be(0);
        settled.Should().BeEmpty();
        _logger.Verify(
            l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.Is<Exception?>(e => ReferenceEquals(e, boom)),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce(),
            "a handler exception is logged");
    }

    [Fact]
    public async Task Replay_HandlerCancelled_PropagatesAfterTheStoreIsSaved()
    {
        var done = Write(WidgetKind, "7", TimeSpan.FromMinutes(10));
        var cancelled = Write(OtherKind, "7", TimeSpan.FromMinutes(5));
        Seed(done, cancelled);
        var completes = ScriptedWriteHandler.Returning(WidgetKind, Result.Success());
        var cancels = new ScriptedWriteHandler(OtherKind, (_, _) => Task.FromException<Result>(new OperationCanceledException()));
        var queue = CreateQueue(completes, cancels);

        var act = () => queue.ReplayAsync(Ct);

        await act.Should().ThrowAsync<OperationCanceledException>();
        Stored().Should().ContainSingle("the entry settled before the cancellation is saved as removed")
            .Which.Id.Should().Be(cancelled.Id);
    }

    // Rule 14: settle events and count
    [Fact]
    public async Task Replay_RaisesSettledAfterTheStoreDropsTheEntry_ReturnsSettledCount()
    {
        var expired = Write(WidgetKind, "7", TimeSpan.FromHours(30));
        var ok = Write(WidgetKind, "7", TimeSpan.FromMinutes(10));
        var refused = Write(OtherKind, "7", TimeSpan.FromMinutes(5));
        var unknown = Write(UnknownKind, "7", TimeSpan.FromMinutes(1));
        Seed(expired, ok, refused, unknown);
        var queue = CreateQueue(
            ScriptedWriteHandler.Returning(WidgetKind, Result.Success()),
            ScriptedWriteHandler.Returning(OtherKind, Result.Failure(Error.Validation("Widget.Invalid", "Invalid."))));
        var seen = new List<(Guid Id, OfflineWriteOutcome Outcome, bool StillStored)>();
        queue.WriteSettled += (_, args) => seen.Add((args.Write.Id, args.Outcome, Stored().Exists(w => w.Id == args.Write.Id)));

        var count = await queue.ReplayAsync(Ct);

        count.Should().Be(3);
        seen.Should().HaveCount(3, "one event per settled entry");
        seen.Select(s => s.Id).Should().OnlyHaveUniqueItems();
        seen.Should().AllSatisfy(s => s.StillStored.Should().BeFalse("the event is raised after the store no longer holds the entry"));
        var outcomes = seen.ToDictionary(s => s.Id, s => s.Outcome);
        outcomes.Should().HaveCount(3);
        outcomes[expired.Id].Should().Be(OfflineWriteOutcome.Expired);
        outcomes[ok.Id].Should().Be(OfflineWriteOutcome.Completed);
        outcomes[refused.Id].Should().Be(OfflineWriteOutcome.Rejected);
        Stored().Select(w => w.Id).Should().Equal(unknown.Id);
    }

    // Rule 15: connectivity trigger
    [Fact]
    public async Task ConnectivityBackOnline_StartsReplay()
    {
        _connectivity.SetOnline(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedWriteHandler(WidgetKind, (_, _) =>
        {
            entered.TrySetResult();
            return gate.Task;
        });
        Seed(Write(WidgetKind, "7"));
        CreateQueue(handler);

        // The event source must get control back while the replay is still running: the handler
        // below is held open, so a queue that awaited (or blocked on) the replay would time out here.
        await Task.Run(() => _connectivity.SetOnline(true), Ct).WaitAsync(WaitBudget, TimeProvider.System, Ct);

        await entered.Task.WaitAsync(WaitBudget, TimeProvider.System, Ct);
        gate.SetResult(Result.Success());
        await Eventually(() => Stored().Count == 0, "the replay started by the reconnect settles the entry");
        Stored().Should().BeEmpty("the replay started by the reconnect settles the entry");
    }

    [Fact]
    public async Task ConnectivityChanged_ToOffline_DoesNotReplay()
    {
        var handler = ScriptedWriteHandler.Returning(WidgetKind, Result.Success());
        Seed(Write(WidgetKind, "7"));
        CreateQueue(handler);

        _connectivity.SetOnline(false);
        await Task.Delay(200, Ct);

        handler.Calls.Should().BeEmpty();
        Stored().Should().ContainSingle();
    }

    [Fact]
    public Task ConnectivityBackOnline_ReplayFault_IsLoggedNotThrown()
    {
        _connectivity.SetOnline(false);
        CreateQueue(ScriptedWriteHandler.Returning(WidgetKind, Result.Success()));
        _store.ThrowOnRead = true;

        var raise = () => _connectivity.SetOnline(true);

        raise.Should().NotThrow("a fault in the background replay never reaches the event source");
        return Eventually(
            () => _logger.Invocations.Any(i => i.Method.Name == nameof(ILogger.Log) && i.Arguments[3] is InvalidOperationException),
            "the background replay's fault is logged");
    }

    [Fact]
    public async Task Dispose_Unsubscribes()
    {
        _connectivity.SetOnline(false);
        var handler = ScriptedWriteHandler.Returning(WidgetKind, Result.Success());
        Seed(Write(WidgetKind, "7"));
        var queue = CreateQueue(handler);
        _connectivity.SubscriberCount.Should().Be(1, "the queue listens for the device coming back online");

        queue.Dispose();

        _connectivity.SubscriberCount.Should().Be(0);
        _connectivity.SetOnline(true);
        await Task.Delay(200, Ct);
        handler.Calls.Should().BeEmpty();
    }

    // Rule 16: PendingWrite helpers
    [Fact]
    public void PendingWrite_IfMatch_FormatsWeakTag()
    {
        byte[] rowVersion = [0, 0, 0, 0, 0, 0, 7, 209];

        var write = Write(WidgetKind, "7") with { BasedOnRowVersion = rowVersion };

        write.IfMatch.Should().Be(ConcurrencyETag.Format(rowVersion));
        write.IfMatch.Should().StartWith("W/\"");
    }

    [Fact]
    public void PendingWrite_IfMatch_NullWithoutRowVersion() =>
        Write(WidgetKind, "7").IfMatch.Should().BeNull();

    [Fact]
    public void PendingWrite_IfMatch_IsNotSerialized()
    {
        var write = Write(WidgetKind, "7") with { BasedOnRowVersion = [1, 2, 3] };

        JsonSerializer.Serialize(write).Should().NotContain("IfMatch");
        JsonSerializer.Serialize(write, JsonSerializerOptions.Web).Should().NotContain("ifMatch");
    }

    [Fact]
    public void PendingWrite_ReadPayload_RoundTrips()
    {
        var payload = new WidgetPayload(5, "Front row");
        var write = Write(WidgetKind, "7") with { PayloadJson = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web) };

        write.ReadPayload<WidgetPayload>().Should().Be(payload);

        // Web options read camelCase, which default options would not bind.
        var camel = Write(WidgetKind, "7") with { PayloadJson = """{"widgetId":9,"note":"camel"}""" };
        camel.ReadPayload<WidgetPayload>().Should().Be(new WidgetPayload(9, "camel"));
    }

    // Rule 19: sign-out clears the store, and with it the queue
    [Fact]
    public async Task SignOutStoreClear_EmptiesTheQueue()
    {
        var queue = CreateQueue();
        await queue.EnqueueAsync(WidgetKind, new WidgetPayload(1, "a"), cancellationToken: Ct);

        await _store.ClearAsync(Ct);

        (await queue.GetPendingAsync(Ct)).Should().BeEmpty();
    }

    private static Error TransientError(string kind) => kind switch
    {
        "timeout" => Error.Failure(HttpResultExecutor.TimeoutErrorCode, "Timed out."),
        "unexpected" => Error.Unexpected("Http.500", "Server error."),
        "too-many-requests" => Error.TooManyRequests("Http.429", "Slow down."),
        "unauthorized" => Error.Unauthorized("Http.401", "Sign in again."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static Error PermanentError(string kind) => kind switch
    {
        "not-found" => Error.NotFoundError("Widget.NotFound", "Gone."),
        "forbidden" => Error.Forbidden("Http.403", "Not yours."),
        "failure" => Error.Failure("Widget.Broken", "Refused."),
        "unprocessable" => Error.UnprocessableEntity("Widget.Unprocessable", "Cannot process."),
        "invariant" => Error.Invariant("Widget.Invariant", "Rule broken."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static async Task Eventually(Func<bool> condition, string because)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > WaitBudget)
            {
                throw new TimeoutException(because);
            }

            await Task.Delay(20, Ct);
        }
    }

    private PendingWrite Write(string kind, string? scope, TimeSpan? age = null, string? coalesceKey = null) => new()
    {
        Id = Guid.NewGuid(),
        Kind = kind,
        IdempotencyKey = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
        PayloadJson = "{}",
        CoalesceKey = coalesceKey,
        UserScope = scope,
        EnqueuedOn = _time.GetUtcNow() - (age ?? TimeSpan.FromMinutes(1)),
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

using AwesomeAssertions;
using MMCA.Common.Aspire.Health;
using Moq;
using RabbitMQ.Client;

namespace MMCA.Common.Aspire.Tests.Health;

/// <summary>
/// The RabbitMQ health check's connection holder: the package's check never disposes the connection its
/// factory returns, so a factory that opened one per probe round leaked an AMQP connection on the
/// broker every round. The holder keeps at most one, replaces it only when it is no longer open,
/// creates it under the check timeout with single flight, caches no failure, and closes it when the
/// host stops. Connections are fakes; no broker is involved.
/// </summary>
public sealed class RabbitMqHealthConnectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task TwoRounds_ReuseOneConnection()
    {
        var factory = new FakeConnections();
        await using var holder = new RabbitMqHealthConnection(factory.CreateAsync, Timeout);

        var first = await holder.GetAsync(TestContext.Current.CancellationToken);
        var second = await holder.GetAsync(TestContext.Current.CancellationToken);

        second.Should().BeSameAs(first);
        factory.Created.Should().ContainSingle("a probe round must not open a connection of its own");
    }

    [Fact]
    public async Task AClosedConnection_IsDisposedAndReplacedOnTheNextRound()
    {
        var factory = new FakeConnections();
        await using var holder = new RabbitMqHealthConnection(factory.CreateAsync, Timeout);
        var first = await holder.GetAsync(TestContext.Current.CancellationToken);

        factory.Created[0].IsOpen = false;
        var second = await holder.GetAsync(TestContext.Current.CancellationToken);

        second.Should().NotBeSameAs(first);
        factory.Created.Should().HaveCount(2);
        factory.Created[0].Disposals.Should().Be(1, "the closed connection is released, not leaked");
        factory.Created[1].Disposals.Should().Be(0);
    }

    [Fact]
    public async Task ADisposeFailure_OnTheClosedConnection_IsSwallowed()
    {
        var factory = new FakeConnections();
        await using var holder = new RabbitMqHealthConnection(factory.CreateAsync, Timeout);
        await holder.GetAsync(TestContext.Current.CancellationToken);
        factory.Created[0].IsOpen = false;
        factory.Created[0].DisposeThrows = true;

        var second = await holder.GetAsync(TestContext.Current.CancellationToken);

        second.Should().BeSameAs(factory.Created[1].Object);
    }

    [Fact]
    public async Task AFailedCreation_IsNotCached_AndTheNextRoundTriesAgain()
    {
        var factory = new FakeConnections { FailuresBeforeSuccess = 1 };
        await using var holder = new RabbitMqHealthConnection(factory.CreateAsync, Timeout);

        var first = () => holder.GetAsync(TestContext.Current.CancellationToken);
        await first.Should().ThrowAsync<IOException>();
        var second = await holder.GetAsync(TestContext.Current.CancellationToken);

        factory.Attempts.Should().Be(2);
        second.Should().BeSameAs(factory.Created.Single().Object);
    }

    [Fact]
    public async Task TenConcurrentFirstCalls_CreateExactlyOneConnection()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeConnections { Gate = release.Task };
        await using var holder = new RabbitMqHealthConnection(factory.CreateAsync, Timeout);

        var calls = Enumerable.Range(0, 10)
            .Select(_ => holder.GetAsync(TestContext.Current.CancellationToken))
            .ToList();
        release.SetResult();
        var connections = await Task.WhenAll(calls);

        factory.Attempts.Should().Be(1, "concurrent callers share one creation");
        connections.Should().AllSatisfy(connection => connection.Should().BeSameAs(connections[0]));
    }

    [Fact]
    public async Task DisposingTheHolder_DisposesTheConnection_AndALaterCallThrowsObjectDisposedException()
    {
        var factory = new FakeConnections();
        var holder = new RabbitMqHealthConnection(factory.CreateAsync, Timeout);
        await holder.GetAsync(TestContext.Current.CancellationToken);

        await holder.DisposeAsync();
        await holder.DisposeAsync();
        var later = () => holder.GetAsync(TestContext.Current.CancellationToken);

        factory.Created.Single().Disposals.Should().Be(1, "the host stopping closes the held connection once");
        await later.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task ACreationThatFinishesAfterDispose_IsDisposed_AndNotHeld()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeConnections { Gate = release.Task };
        var holder = new RabbitMqHealthConnection(factory.CreateAsync, Timeout);
        var pending = holder.GetAsync(TestContext.Current.CancellationToken);

        await holder.DisposeAsync();
        release.SetResult();

        await pending.Invoking(task => task).Should().ThrowAsync<ObjectDisposedException>();
        factory.Created.Single().Disposals.Should().Be(1, "a connection nobody can use any more is closed");
    }

    [Fact]
    public async Task ACreationThatNeverCompletes_EndsAtTheTimeout_AndLeavesNothingHeld()
    {
        var never = new TaskCompletionSource<IConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeConnections { FirstCreation = never.Task };
        await using var holder = new RabbitMqHealthConnection(factory.CreateAsync, TimeSpan.FromMilliseconds(300));
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        var first = () => holder.GetAsync(TestContext.Current.CancellationToken);
        await first.Should().ThrowAsync<TimeoutException>();
        elapsed.Stop();
        var second = await holder.GetAsync(TestContext.Current.CancellationToken);

        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "the check timeout bounds the creation");
        factory.Attempts.Should().Be(2, "the abandoned creation is not held, so the next round creates again");
        second.Should().BeSameAs(factory.Created.Single().Object);
        factory.LastToken.CanBeCanceled.Should().BeTrue();
        never.SetCanceled(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ACreationThatHonoursItsToken_IsCancelledAtTheTimeout()
    {
        var factory = new FakeConnections { HonourTokenForever = true };
        await using var holder = new RabbitMqHealthConnection(factory.CreateAsync, TimeSpan.FromMilliseconds(300));

        var act = () => holder.GetAsync(TestContext.Current.CancellationToken);

        // The token and the wait cut-off both fire at the timeout, so either may surface first.
        var thrown = (await act.Should().ThrowAsync<Exception>()).Which;
        thrown.Should().Match<Exception>(exception => exception is OperationCanceledException || exception is TimeoutException);
        factory.LastToken.IsCancellationRequested.Should().BeTrue("the creation's token is cancelled at the timeout");
        factory.Attempts.Should().Be(1);
    }

    /// <summary>A connection fake whose open state and dispose behaviour the test drives.</summary>
    private sealed class FakeConnection
    {
        private int _disposals;

        public FakeConnection()
        {
            Mock.SetupGet(connection => connection.IsOpen).Returns(() => IsOpen);
            Mock.Setup(connection => connection.DisposeAsync()).Returns(() =>
            {
                Interlocked.Increment(ref _disposals);
                return DisposeThrows
                    ? ValueTask.FromException(new IOException("the socket is already gone"))
                    : ValueTask.CompletedTask;
            });
        }

        public Mock<IConnection> Mock { get; } = new(MockBehavior.Loose);

        public IConnection Object => Mock.Object;

        public bool IsOpen { get; set; } = true;

        public bool DisposeThrows { get; set; }

        public int Disposals => Volatile.Read(ref _disposals);
    }

    /// <summary>The injectable creation delegate, recording every attempt.</summary>
    private sealed class FakeConnections
    {
        private int _attempts;

        public List<FakeConnection> Created { get; } = [];

        public int Attempts => Volatile.Read(ref _attempts);

        public int FailuresBeforeSuccess { get; init; }

        public Task? Gate { get; init; }

        public Task<IConnection>? FirstCreation { get; init; }

        public bool HonourTokenForever { get; init; }

        public CancellationToken LastToken { get; private set; }

        public async Task<IConnection> CreateAsync(CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            LastToken = cancellationToken;
            if (attempt == 1 && FirstCreation is not null)
            {
                return await FirstCreation;
            }

            if (HonourTokenForever)
            {
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (Gate is not null)
            {
                await Gate;
            }

            if (attempt <= FailuresBeforeSuccess)
            {
                throw new IOException("connection.start was never received");
            }

            var connection = new FakeConnection();
            lock (Created)
            {
                Created.Add(connection);
            }

            return connection.Object;
        }
    }
}

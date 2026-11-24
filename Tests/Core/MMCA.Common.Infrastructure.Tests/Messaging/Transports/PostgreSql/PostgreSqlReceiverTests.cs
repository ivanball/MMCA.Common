using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Infrastructure.Messaging.Transports.PostgreSql;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Messaging.Transports.PostgreSql;

/// <summary>
/// The PostgreSQL receiver loop over an in-memory store: startup migration and subscription, each
/// claimed row through the pipeline and settled by its disposition, and a row cut short by the host
/// stopping released rather than settled.
/// </summary>
public sealed class PostgreSqlReceiverTests
{
    private static readonly BrokerTopology Topology = new(
        "demo",
        "demo-billing",
        [
            new BrokerSubscription("order-placed", typeof(object), "demo-order-placed", BrokerConsumerKind.Current),
            new BrokerSubscription("order-cancelled", typeof(string), "demo-order-cancelled", BrokerConsumerKind.Current),
        ]);

    private static readonly string[] ExpectedTopics = ["demo-order-placed", "demo-order-cancelled"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PostgreSqlReceiverOptions Options(bool migrate = true, bool provision = true) =>
        new(Concurrency: 2, ClaimBatchSize: 2, migrate, provision, Lease: TimeSpan.FromSeconds(30), RenewInterval: TimeSpan.FromSeconds(10), PollInterval: TimeSpan.FromMilliseconds(50));

    private static PostgreSqlReceiver Receiver(FakePostgreSqlMessageStore store, IBrokerReceivePipeline pipeline, PostgreSqlReceiverOptions? options = null) =>
        new(Topology, pipeline, store, options ?? Options(), NullLogger<PostgreSqlReceiver>.Instance);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the receiver should have reached the expected state");
            await Task.Delay(20, Ct);
        }
    }

    [Fact]
    public async Task Start_MigratesAndSubscribesTheQueueToEveryTopic()
    {
        var store = new FakePostgreSqlMessageStore();
        using var receiver = Receiver(store, Mock.Of<IBrokerReceivePipeline>());

        await receiver.StartAsync(Ct);
        await WaitUntilAsync(() => !store.Subscriptions.IsEmpty);
        await receiver.StopAsync(Ct);

        store.SchemaCalls.Should().Be(1);
        store.Subscriptions.Should().ContainSingle().Which.Should().BeEquivalentTo(
            ("demo-billing", ExpectedTopics));
    }

    [Fact]
    public async Task Start_WithoutMigrationOrProvisioning_TouchesNeither()
    {
        var store = new FakePostgreSqlMessageStore();
        var row = store.Enqueue();
        var pipeline = new Mock<IBrokerReceivePipeline>();
        pipeline.Setup(p => p.ProcessAsync(It.IsAny<IncomingBrokerMessage>(), It.IsAny<CancellationToken>())).ReturnsAsync(BrokerDisposition.Complete);
        using var receiver = Receiver(store, pipeline.Object, Options(migrate: false, provision: false));

        await receiver.StartAsync(Ct);
        await WaitUntilAsync(() => !store.Completed.IsEmpty);
        await receiver.StopAsync(Ct);

        store.SchemaCalls.Should().Be(0);
        store.Subscriptions.Should().BeEmpty();
        store.Completed.Should().ContainSingle().Which.Should().BeSameAs(row);
    }

    [Fact]
    public async Task EachClaimedRow_GoesThroughThePipeline_AndIsSettledByItsDisposition()
    {
        var store = new FakePostgreSqlMessageStore();
        var complete = store.Enqueue("complete");
        var redeliver = store.Enqueue("redeliver");
        var deadLetter = store.Enqueue("dead-letter");
        List<IncomingBrokerMessage> seen = [];
        var pipeline = new Mock<IBrokerReceivePipeline>();
        pipeline.Setup(p => p.ProcessAsync(It.IsAny<IncomingBrokerMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IncomingBrokerMessage m, CancellationToken _) =>
            {
                lock (seen)
                {
                    seen.Add(m);
                }

                return m.EventName switch
                {
                    "redeliver" => new BrokerDisposition.Redeliver(TimeSpan.FromSeconds(60), 1),
                    "dead-letter" => new BrokerDisposition.DeadLetter("poison", null),
                    _ => BrokerDisposition.Complete,
                };
            });
        using var receiver = Receiver(store, pipeline.Object);

        await receiver.StartAsync(Ct);
        await WaitUntilAsync(() => store.Completed.Count + store.Redelivered.Count + store.DeadLettered.Count == 3);
        await receiver.StopAsync(Ct);

        store.Completed.Should().ContainSingle().Which.Should().BeSameAs(complete);
        store.Redelivered.Should().ContainSingle().Which.Message.Should().BeSameAs(redeliver);
        store.DeadLettered.Should().ContainSingle().Which.Message.Should().BeSameAs(deadLetter);
        store.Released.Should().BeEmpty();
        seen.Should().HaveCount(3).And.AllSatisfy(m => m.BrokerMessageId.Should().NotBeEmpty());
        seen.Single(m => m.EventName == "complete").Body.ToArray().Should().Equal(complete.Body);
    }

    [Fact]
    public async Task HostStopping_MidProcessing_ReleasesTheClaim()
    {
        var store = new FakePostgreSqlMessageStore();
        var row = store.Enqueue();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new Mock<IBrokerReceivePipeline>();
        pipeline.Setup(p => p.ProcessAsync(It.IsAny<IncomingBrokerMessage>(), It.IsAny<CancellationToken>()))
            .Returns(async (IncomingBrokerMessage _, CancellationToken token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return BrokerDisposition.Complete;
            });
        using var receiver = Receiver(store, pipeline.Object);

        await receiver.StartAsync(Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await receiver.StopAsync(Ct);

        store.Released.Should().ContainSingle().Which.Should().BeSameAs(row);
        store.Completed.Should().BeEmpty();
    }

    [Fact]
    public async Task ARowStillInFlight_HasItsLeaseRenewedEveryRenewInterval()
    {
        var store = new FakePostgreSqlMessageStore();
        var row = store.Enqueue();
        var pipeline = new Mock<IBrokerReceivePipeline>();
        pipeline.Setup(p => p.ProcessAsync(It.IsAny<IncomingBrokerMessage>(), It.IsAny<CancellationToken>()))
            .Returns(async (IncomingBrokerMessage _, CancellationToken token) =>
            {
                // Held until two renewals have gone out for it.
                while (store.Renewed.Count < 2)
                {
                    await Task.Delay(10, token);
                }

                return BrokerDisposition.Complete;
            });
        var options = Options() with { Lease = TimeSpan.FromSeconds(3), RenewInterval = TimeSpan.FromMilliseconds(100) };
        using var receiver = Receiver(store, pipeline.Object, options);

        await receiver.StartAsync(Ct);
        await WaitUntilAsync(() => !store.Completed.IsEmpty);
        await receiver.StopAsync(Ct);

        store.Renewed.Should().HaveCountGreaterThanOrEqualTo(2).And.AllSatisfy(renewal =>
        {
            renewal.Ids.Should().Equal(row.Id);
            renewal.Lease.Should().Be(TimeSpan.FromSeconds(3), "each renewal extends the claim by the full lease");
        });
        store.Completed.Should().ContainSingle().Which.Should().BeSameAs(row);
    }

    [Fact]
    public async Task APipelineThatThrows_ReleasesTheClaimForAnotherAttempt()
    {
        var store = new FakePostgreSqlMessageStore();
        var row = store.Enqueue();
        var pipeline = new Mock<IBrokerReceivePipeline>();
        pipeline.Setup(p => p.ProcessAsync(It.IsAny<IncomingBrokerMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("unexpected"));
        using var receiver = Receiver(store, pipeline.Object);

        await receiver.StartAsync(Ct);
        await WaitUntilAsync(() => !store.Released.IsEmpty);
        await receiver.StopAsync(Ct);

        store.Released.Should().Contain(row);
        store.Completed.Should().BeEmpty();
    }

    [Fact]
    public void Options_FromSettings_ResolveBackpressure()
    {
        var options = PostgreSqlReceiverOptions.From(
            new Infrastructure.Messaging.MessageBusSettings { ConcurrentMessageLimit = 4, PrefetchCount = 10, ProvisionTopology = false },
            migrateOnStartup: true);

        options.Concurrency.Should().Be(4);
        options.ClaimBatchSize.Should().Be(4, "a claim never takes more rows than there are free slots");
        options.ProvisionTopology.Should().BeFalse();
        options.MigrateOnStartup.Should().BeTrue();
        options.Lease.Should().Be(PostgreSqlReceiverOptions.DefaultLease);
        options.RenewInterval.Should().Be(PostgreSqlReceiverOptions.DefaultRenewInterval);
        options.RenewInterval.Should().BeLessThan(options.Lease, "a lease is renewed before it lapses");
    }

    [Fact]
    public void Options_Unset_DefaultToTheProcessorCount()
    {
        var options = PostgreSqlReceiverOptions.From(new Infrastructure.Messaging.MessageBusSettings(), migrateOnStartup: false);

        options.Concurrency.Should().Be(Environment.ProcessorCount);
        options.ClaimBatchSize.Should().Be(Environment.ProcessorCount);
        options.ProvisionTopology.Should().BeTrue();
    }
}

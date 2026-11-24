using AwesomeAssertions;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Infrastructure.Messaging.Transports.RabbitMq;

namespace MMCA.Common.Infrastructure.Tests.Messaging.Transports.RabbitMq;

/// <summary>Topology provisioning: what is declared, once, and what a host that does not provision checks instead.</summary>
public sealed class RabbitMqProvisionerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BrokerTopology Topology() => new(
        "demo",
        "demo-billing",
        [
            new BrokerSubscription("order-placed", typeof(object), "demo-order-placed", BrokerConsumerKind.Current),
            new BrokerSubscription("order-cancelled", typeof(string), "demo-order-cancelled", BrokerConsumerKind.Current),
        ]);

    [Fact]
    public async Task ProvisionReceive_DeclaresTheQueueTheErrorQueueAndEachExchangeWithItsBinding()
    {
        var operations = new RecordingRabbitMqOperations();

        await new RabbitMqProvisioner(provisionTopology: true).ProvisionReceiveAsync(operations, Topology(), Ct);

        operations.Calls.Should().Equal(
            "queue demo-billing",
            "queue demo-billing-error",
            "exchange demo-order-placed",
            "bind demo-billing demo-order-placed",
            "exchange demo-order-cancelled",
            "bind demo-billing demo-order-cancelled");
        operations.QueueArguments["demo-billing"].Should().BeNull("the host queue is a plain durable queue");
    }

    [Fact]
    public async Task ProvisionReceive_NotProvisioning_DeclaresNothing()
    {
        var operations = new RecordingRabbitMqOperations();

        await new RabbitMqProvisioner(provisionTopology: false).ProvisionReceiveAsync(operations, Topology(), Ct);

        operations.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureExchange_DeclaresOnce_AndAgainAfterItIsForgotten()
    {
        var operations = new RecordingRabbitMqOperations();
        var provisioner = new RabbitMqProvisioner(provisionTopology: true);

        await provisioner.EnsureExchangeAsync(operations, "demo-order-placed", Ct);
        await provisioner.EnsureExchangeAsync(operations, "demo-order-placed", Ct);
        provisioner.ForgetExchange("demo-order-placed");
        await provisioner.EnsureExchangeAsync(operations, "demo-order-placed", Ct);

        operations.Calls.Should().Equal("exchange demo-order-placed", "exchange demo-order-placed");
    }

    [Fact]
    public async Task EnsureExchange_NotProvisioning_DeclaresNothing()
    {
        var operations = new RecordingRabbitMqOperations();

        await new RabbitMqProvisioner(provisionTopology: false).EnsureExchangeAsync(operations, "demo-order-placed", Ct);

        operations.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureDelayQueue_DeclaresLazilyOncePerInterval_WithTheTtlArguments()
    {
        var operations = new RecordingRabbitMqOperations();
        var provisioner = new RabbitMqProvisioner(provisionTopology: true);

        var first = await provisioner.EnsureDelayQueueAsync(operations, "demo-billing", 60, Ct);
        await provisioner.EnsureDelayQueueAsync(operations, "demo-billing", 60, Ct);
        var second = await provisioner.EnsureDelayQueueAsync(operations, "demo-billing", 600, Ct);

        first.Should().Be("demo-billing-delay-60s");
        second.Should().Be("demo-billing-delay-600s");
        operations.Calls.Should().Equal("queue demo-billing-delay-60s", "queue demo-billing-delay-600s");
        operations.QueueArguments["demo-billing-delay-60s"].Should().BeEquivalentTo(RabbitMqNames.DelayQueueArguments("demo-billing", 60));
    }

    [Fact]
    public async Task EnsureErrorQueue_AfterProvisioning_IsNotDeclaredAgain()
    {
        var operations = new RecordingRabbitMqOperations();
        var provisioner = new RabbitMqProvisioner(provisionTopology: true);
        await provisioner.ProvisionReceiveAsync(operations, Topology(), Ct);
        operations.Calls.Clear();

        var errorQueue = await provisioner.EnsureErrorQueueAsync(operations, "demo-billing", Ct);

        errorQueue.Should().Be("demo-billing-error");
        operations.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task NotProvisioning_DelayAndErrorQueuesAreCheckedPassivelyOnce()
    {
        var operations = new RecordingRabbitMqOperations();
        var provisioner = new RabbitMqProvisioner(provisionTopology: false);

        await provisioner.EnsureDelayQueueAsync(operations, "demo-billing", 60, Ct);
        await provisioner.EnsureDelayQueueAsync(operations, "demo-billing", 60, Ct);
        await provisioner.EnsureErrorQueueAsync(operations, "demo-billing", Ct);

        operations.Calls.Should().Equal("passive demo-billing-delay-60s", "passive demo-billing-error");
    }
}

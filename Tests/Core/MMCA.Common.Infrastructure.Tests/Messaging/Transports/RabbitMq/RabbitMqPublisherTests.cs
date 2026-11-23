using AwesomeAssertions;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Infrastructure.Messaging.Transports.RabbitMq;

namespace MMCA.Common.Infrastructure.Tests.Messaging.Transports.RabbitMq;

/// <summary>The RabbitMQ publisher: exchange per topic, declared before the first publish when provisioning.</summary>
public sealed class RabbitMqPublisherTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OutgoingBrokerMessage Message(string topic = "demo-order-placed") => new(
        topic,
        "order-placed",
        Guid.NewGuid(),
        new byte[] { 7 },
        new Dictionary<string, string>(StringComparer.Ordinal) { [BrokerHeaders.EventName] = "order-placed" });

    [Fact]
    public async Task Publish_DeclaresTheExchangeOnce_ThenPublishesToItUnmandatory()
    {
        var operations = new RecordingRabbitMqOperations();
        var publisher = new RabbitMqPublisher(operations, new RabbitMqProvisioner(provisionTopology: true));

        await publisher.PublishAsync(Message(), Ct);
        await publisher.PublishAsync(Message(), Ct);

        operations.Calls.Should().Equal("exchange demo-order-placed", "publish demo-order-placed/", "publish demo-order-placed/");
        operations.Publishes.Should().AllSatisfy(p =>
        {
            p.Mandatory.Should().BeFalse("a topic nobody consumes yet is not a publish failure");
            p.Headers[BrokerHeaders.EventName].Should().Be("order-placed");
        });
    }

    [Fact]
    public async Task Publish_NotProvisioning_OnlyPublishes()
    {
        var operations = new RecordingRabbitMqOperations();
        var publisher = new RabbitMqPublisher(operations, new RabbitMqProvisioner(provisionTopology: false));

        await publisher.PublishAsync(Message(), Ct);

        operations.Calls.Should().Equal("publish demo-order-placed/");
    }

    [Fact]
    public async Task Publish_Failure_Throws_AndRedeclaresTheExchangeNextTime()
    {
        var operations = new RecordingRabbitMqOperations();
        var publisher = new RabbitMqPublisher(operations, new RabbitMqProvisioner(provisionTopology: true));
        await publisher.PublishAsync(Message(), Ct);
        operations.PublishFailure = new InvalidOperationException("NOT_FOUND");

        var act = () => publisher.PublishAsync(Message(), Ct);
        await act.Should().ThrowAsync<InvalidOperationException>();
        await publisher.PublishAsync(Message(), Ct);

        operations.Calls.Should().Equal(
            "exchange demo-order-placed",
            "publish demo-order-placed/",
            "publish demo-order-placed/",
            "exchange demo-order-placed",
            "publish demo-order-placed/");
    }

    [Fact]
    public async Task Publish_LongTopic_UsesTheFittedExchangeName()
    {
        var operations = new RecordingRabbitMqOperations();
        var publisher = new RabbitMqPublisher(operations, new RabbitMqProvisioner(provisionTopology: true));
        var topic = new string('t', 400);

        await publisher.PublishAsync(Message(topic), Ct);

        operations.Publishes.Should().ContainSingle().Which.Exchange.Should().Be(RabbitMqNames.Exchange(topic)).And.HaveLength(RabbitMqNames.MaxNameLength);
    }
}

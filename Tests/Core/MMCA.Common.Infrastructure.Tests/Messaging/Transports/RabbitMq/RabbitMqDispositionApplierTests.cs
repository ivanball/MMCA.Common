using AwesomeAssertions;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Infrastructure.Messaging.Transports.RabbitMq;

namespace MMCA.Common.Infrastructure.Tests.Messaging.Transports.RabbitMq;

/// <summary>
/// Applying the pipeline's disposition before the original is acknowledged: nothing for Complete, a
/// confirmed mandatory copy to the delay queue for Redeliver, and to the error queue for DeadLetter.
/// </summary>
public sealed class RabbitMqDispositionApplierTests
{
    private static readonly byte[] Body = [1, 2, 3];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IncomingBrokerMessage Delivered() => new(
        Body,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BrokerHeaders.EventName] = "order-placed",
            [BrokerHeaders.RedeliveryCount] = "1",
            ["MMCA-UserId"] = "42",
        },
        "7");

    [Fact]
    public async Task Complete_PublishesNothing()
    {
        var operations = new RecordingRabbitMqOperations();

        await RabbitMqDispositionApplier.ApplyAsync(operations, new RabbitMqProvisioner(true), "demo-billing", Delivered(), BrokerDisposition.Complete, Ct);

        operations.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Redeliver_PublishesACopyToTheDelayQueue_WithTheNewCount()
    {
        var operations = new RecordingRabbitMqOperations();

        await RabbitMqDispositionApplier.ApplyAsync(
            operations,
            new RabbitMqProvisioner(true),
            "demo-billing",
            Delivered(),
            new BrokerDisposition.Redeliver(TimeSpan.FromMinutes(10), 2),
            Ct);

        operations.Calls.Should().Equal("queue demo-billing-delay-600s", "publish /demo-billing-delay-600s");
        var copy = operations.Publishes.Should().ContainSingle().Subject;
        copy.Exchange.Should().BeEmpty("the default exchange routes by queue name");
        copy.Mandatory.Should().BeTrue("an unroutable copy must fail rather than vanish");
        copy.Body.Should().Equal(Body);
        copy.Headers.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            [BrokerHeaders.EventName] = "order-placed",
            [BrokerHeaders.RedeliveryCount] = "2",
            ["MMCA-UserId"] = "42",
        });
    }

    [Fact]
    public async Task DeadLetter_PublishesACopyToTheErrorQueue_WithReasonAndDetail()
    {
        var operations = new RecordingRabbitMqOperations();

        await RabbitMqDispositionApplier.ApplyAsync(
            operations,
            new RabbitMqProvisioner(true),
            "demo-billing",
            Delivered(),
            new BrokerDisposition.DeadLetter("handler-failed", "InvalidOperationException: boom"),
            Ct);

        var copy = operations.Publishes.Should().ContainSingle().Subject;
        copy.RoutingKey.Should().Be("demo-billing-error");
        copy.Mandatory.Should().BeTrue();
        copy.Headers[RabbitMqNames.DeadLetterReasonHeader].Should().Be("handler-failed");
        copy.Headers[RabbitMqNames.DeadLetterDetailHeader].Should().Be("InvalidOperationException: boom");
        copy.Headers[BrokerHeaders.EventName].Should().Be("order-placed");
    }

    [Fact]
    public async Task DeadLetter_WithoutDetail_OmitsTheDetailHeader()
    {
        var operations = new RecordingRabbitMqOperations();

        await RabbitMqDispositionApplier.ApplyAsync(
            operations,
            new RabbitMqProvisioner(true),
            "demo-billing",
            Delivered(),
            new BrokerDisposition.DeadLetter("unknown-event", null),
            Ct);

        operations.Publishes.Should().ContainSingle().Which.Headers.Should().NotContainKey(RabbitMqNames.DeadLetterDetailHeader);
    }

    [Fact]
    public async Task AFailedCopy_Throws_SoTheOriginalIsNotAcknowledged()
    {
        var operations = new RecordingRabbitMqOperations { PublishFailure = new InvalidOperationException("nacked") };

        var act = () => RabbitMqDispositionApplier.ApplyAsync(
            operations,
            new RabbitMqProvisioner(true),
            "demo-billing",
            Delivered(),
            new BrokerDisposition.Redeliver(TimeSpan.FromSeconds(60), 1),
            Ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("nacked");
    }
}

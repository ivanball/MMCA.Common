using AwesomeAssertions;
using MMCA.Common.Infrastructure.Messaging.Transports.RabbitMq;

namespace MMCA.Common.Infrastructure.Tests.Messaging.Transports.RabbitMq;

/// <summary>The RabbitMQ entity names: exchange per topic, host queue, error queue, per-interval delay queue.</summary>
public sealed class RabbitMqNamesTests
{
    [Fact]
    public void Names_FollowTheTopologyNames()
    {
        RabbitMqNames.Exchange("demo-orders-order-placed").Should().Be("demo-orders-order-placed");
        RabbitMqNames.Queue("demo-billing").Should().Be("demo-billing");
        RabbitMqNames.ErrorQueue("demo-billing").Should().Be("demo-billing-error");
        RabbitMqNames.DelayQueue("demo-billing", 600).Should().Be("demo-billing-delay-600s");
    }

    [Theory]
    [InlineData(0.2, 1)]
    [InlineData(1, 1)]
    [InlineData(1.5, 2)]
    [InlineData(60, 60)]
    [InlineData(3600, 3600)]
    public void DelaySeconds_RoundsUpToWholeSeconds_AtLeastOne(double seconds, long expected) =>
        RabbitMqNames.DelaySeconds(TimeSpan.FromSeconds(seconds)).Should().Be(expected);

    [Fact]
    public void LongNames_AreFittedToTheBrokerLimit_Deterministically()
    {
        var queue = new string('q', 300);

        var first = RabbitMqNames.Queue(queue);
        var delay = RabbitMqNames.DelayQueue(queue, 60);

        first.Length.Should().Be(RabbitMqNames.MaxNameLength);
        RabbitMqNames.Queue(queue).Should().Be(first, "every host must compute the same name");
        delay.Length.Should().BeLessThanOrEqualTo(RabbitMqNames.MaxNameLength);
        RabbitMqNames.ErrorQueue(queue).Should().NotBe(first).And.NotBe(delay);
    }

    [Fact]
    public void DelayQueueArguments_ExpireAfterTheIntervalBackToTheHostQueue()
    {
        var arguments = RabbitMqNames.DelayQueueArguments("demo-billing", 60);

        arguments.Should().HaveCount(3);
        arguments["x-message-ttl"].Should().Be(60_000L);
        arguments["x-dead-letter-exchange"].Should().Be(string.Empty);
        arguments["x-dead-letter-routing-key"].Should().Be("demo-billing");
    }

    [Fact]
    public void DelayQueueArguments_RouteBackToTheFittedQueueName()
    {
        var queue = new string('q', 300);

        RabbitMqNames.DelayQueueArguments(queue, 60)["x-dead-letter-routing-key"].Should().Be(RabbitMqNames.Queue(queue));
    }
}

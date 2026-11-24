using AwesomeAssertions;
using Azure.Messaging.ServiceBus;
using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Azure.ServiceBusEmulator.Tests;

/// <summary>
/// The native Azure Service Bus transport against the official emulator: the topology it provisions
/// (queue, topic per consumed event, a forwarding subscription named after the queue), the publish to
/// consume round trip through the framework's message bus and receive pipeline, a scheduled
/// second-level redelivery, dead-lettering into the queue's native dead-letter queue (handler failure
/// and an unknown event), and the publisher creating a missing topic on its first publish.
/// </summary>
[Collection(ServiceBusEmulatorCollection.Name)]
public sealed class AzureServiceBusEmulatorTransportTests(ServiceBusEmulatorFixture emulator)
{
    private const string Queue = ServiceBusEmulatorFixture.QueueName;

    private static readonly TimeSpan ConsumeTimeout = TimeSpan.FromSeconds(60);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Topology_IsTheQueueAndAForwardingSubscriptionPerConsumedTopic()
    {
        (await emulator.AdminClient.QueueExistsAsync(Queue, Token)).Value.Should().BeTrue();

        var topic = BrokerNaming.Topic(Queue, BrokerNaming.EventName(typeof(RoundTripEvent)));
        var subscription = (await emulator.AdminClient.GetSubscriptionAsync(topic, Queue, Token)).Value;

        subscription.ForwardTo.Should().EndWith(Queue, "the subscription forwards every message to the host's one queue");
    }

    [Fact]
    public async Task APublishedEvent_IsConsumedThroughThePipeline()
    {
        var marker = Guid.NewGuid();

        await emulator.PublishAsync(new RoundTripEvent(marker, "hello"), Token);

        var received = await WaitForAsync<RoundTripEvent>(e => e.Marker == marker);
        received.Should().NotBeNull("the event must travel topic -> subscription -> queue -> processor -> pipeline -> handler");
        received!.Payload.Should().Be("hello");
    }

    [Fact]
    public async Task AFailedDelivery_IsRedeliveredByASchedule_AndThenConsumed()
    {
        var marker = Guid.NewGuid();
        var published = DateTimeOffset.UtcNow;

        await emulator.PublishAsync(new FlakyEvent(marker), Token);

        var received = await WaitForAsync<FlakyEvent>(e => e.Marker == marker);
        received.Should().NotBeNull("the first delivery fails, and the scheduled copy on the queue succeeds");
        emulator.Attempts[marker].Should().Be(2);
        (DateTimeOffset.UtcNow - published).Should().BeGreaterThanOrEqualTo(
            TimeSpan.FromSeconds(ServiceBusEmulatorFixture.RedeliverySeconds - 1),
            "the copy is scheduled at now plus the redelivery interval, not re-enqueued at once");
    }

    [Fact]
    public async Task AnEventThatAlwaysFails_EndsInTheNativeDeadLetterQueue()
    {
        var marker = Guid.NewGuid();
        var integrationEvent = new DoomedEvent(marker);

        await emulator.PublishAsync(integrationEvent, Token);

        var deadLettered = await ReceiveDeadLetterAsync(integrationEvent.MessageId.ToString("D"));
        deadLettered.Should().NotBeNull();
        deadLettered!.DeadLetterReason.Should().Be("handler-failed");
        deadLettered.DeadLetterErrorDescription.Should().Contain("Every delivery fails on purpose.");
        deadLettered.ApplicationProperties[BrokerHeaders.RedeliveryCount].Should().Be("1", "the dead-lettered message is the redelivered copy");
        emulator.Attempts[marker].Should().Be(2, "one delivery plus the one configured redelivery");
    }

    [Fact]
    public async Task AnUnknownEvent_IsDeadLetteredWithoutRetry()
    {
        var messageId = Guid.NewGuid().ToString("D");
        var sender = emulator.Client.CreateSender(Queue);
        await using (sender)
        {
            var message = new ServiceBusMessage(BinaryData.FromString("{}")) { MessageId = messageId, ContentType = BrokerHeaders.JsonContentType };
            message.ApplicationProperties[BrokerHeaders.EventName] = "nobody-consumes-this";
            await sender.SendMessageAsync(message, Token);
        }

        var deadLettered = await ReceiveDeadLetterAsync(messageId);
        deadLettered.Should().NotBeNull();
        deadLettered!.DeadLetterReason.Should().Be("unknown-event");
    }

    [Fact]
    public async Task ThePublisher_CreatesAMissingTopicOnItsFirstPublish()
    {
        var topic = BrokerNaming.Topic(Queue, BrokerNaming.EventName(typeof(PublishOnlyEvent)));

        await emulator.PublishAsync(new PublishOnlyEvent(Guid.NewGuid()), Token);

        (await emulator.AdminClient.TopicExistsAsync(topic, Token)).Value.Should().BeTrue();
    }

    private async Task<TEvent?> WaitForAsync<TEvent>(Func<TEvent, bool> match)
        where TEvent : class
    {
        var deadline = DateTime.UtcNow + ConsumeTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var hit = emulator.Consumed.OfType<TEvent>().FirstOrDefault(match);
            if (hit is not null)
            {
                return hit;
            }

            await Task.Delay(250, Token);
        }

        return null;
    }

    private async Task<ServiceBusReceivedMessage?> ReceiveDeadLetterAsync(string messageId)
    {
        var receiver = emulator.Client.CreateReceiver(Queue, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter, ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete });
        await using (receiver)
        {
            var deadline = DateTime.UtcNow + ConsumeTimeout;
            while (DateTime.UtcNow < deadline)
            {
                var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5), Token);
                if (message is null)
                {
                    continue;
                }

                if (message.MessageId == messageId)
                {
                    return message;
                }
            }

            return null;
        }
    }
}

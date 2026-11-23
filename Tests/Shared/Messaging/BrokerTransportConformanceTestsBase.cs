using System.Globalization;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Tests.Shared.Messaging;

/// <summary>
/// The behaviour every built-in broker transport owes the receive pipeline, run against a REAL broker by
/// each transport's Docker tier: a publish reaches the pipeline with its body and headers intact, a topic
/// fans out to every queue bound to it, a Redeliver disposition comes back after its delay with the new
/// count, a DeadLetter disposition lands in the dead-letter location with its reason and detail, and
/// competing receivers on one queue see each message once. Each test uses fresh queue and topic names,
/// so the tests share one broker without seeing each other's messages.
/// </summary>
public abstract class BrokerTransportConformanceTestsBase
{
    /// <summary>How long a test waits for the broker before failing.</summary>
    protected static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
    private readonly Dictionary<string, int> _receivers = new(StringComparer.Ordinal);

    /// <summary>Gets the test's cancellation token.</summary>
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Publish_ReachesTheSubscribedQueue_WithBodyAndHeadersIntact()
    {
        var pipeline = new ScriptedReceivePipeline();
        await using var host = await StartHostAsync(Name("billing"), pipeline, Name("order-placed"));
        var message = Message(Name("order-placed"));

        await host.Publisher.PublishAsync(message, Ct);

        var delivery = (await pipeline.WaitForAsync(1, Timeout, Ct)).Single();
        delivery.Message.Body.ToArray().Should().Equal(message.Body.ToArray());
        delivery.Message.Headers.Should().BeEquivalentTo(message.Headers, "every header must arrive with its published key case and value");
        delivery.Message.RedeliveryCount.Should().Be(0);
        await AssertSettledAsync(Name("billing"));
    }

    [Fact]
    public async Task Publish_FansOutToEveryQueueBoundToTheTopic()
    {
        var billing = new ScriptedReceivePipeline();
        var shipping = new ScriptedReceivePipeline();
        await using var billingHost = await StartHostAsync(Name("billing"), billing, Name("order-placed"));
        await using var shippingHost = await StartHostAsync(Name("shipping"), shipping, Name("order-placed"), Name("order-cancelled"));

        await billingHost.Publisher.PublishAsync(Message(Name("order-placed")), Ct);

        (await billing.WaitForAsync(1, Timeout, Ct)).Should().ContainSingle();
        (await shipping.WaitForAsync(1, Timeout, Ct)).Should().ContainSingle();
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        billing.Deliveries.Should().ContainSingle("each queue gets exactly one copy");
        shipping.Deliveries.Should().ContainSingle();
    }

    [Fact]
    public async Task Publish_ToATopicNoQueueConsumes_Succeeds()
    {
        await using var host = await StartHostAsync(Name("billing"), new ScriptedReceivePipeline());

        var act = () => host.Publisher.PublishAsync(Message(Name("nobody-listens")), Ct);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Redeliver_ReturnsTheMessageAfterTheDelay_WithTheNewCount()
    {
        var delay = TimeSpan.FromSeconds(2);
        var pipeline = new ScriptedReceivePipeline((_, attempt) =>
            attempt == 1 ? new BrokerDisposition.Redeliver(delay, 1) : BrokerDisposition.Complete);
        await using var host = await StartHostAsync(Name("billing"), pipeline, Name("order-placed"));
        var message = Message(Name("order-placed"));

        await host.Publisher.PublishAsync(message, Ct);

        var deliveries = await pipeline.WaitForAsync(2, Timeout, Ct);
        var (first, second) = (deliveries[0], deliveries[1]);
        second.Message.RedeliveryCount.Should().Be(1);
        second.Message.Headers[BrokerHeaders.RedeliveryCount].Should().Be("1");
        second.Message.Body.ToArray().Should().Equal(message.Body.ToArray());
        second.Message.Headers[BrokerHeaders.MessageId].Should().Be(message.Headers[BrokerHeaders.MessageId]);
        (second.ReceivedAt - first.ReceivedAt).Should().BeGreaterThan(delay - TimeSpan.FromMilliseconds(500), "the copy must stay hidden for the delay");
        await AssertSettledAsync(Name("billing"));
    }

    [Fact]
    public async Task DeadLetter_MovesTheMessageToTheDeadLetterLocation_WithReasonAndDetail()
    {
        var pipeline = new ScriptedReceivePipeline((_, _) => new BrokerDisposition.DeadLetter("handler-failed", "InvalidOperationException: boom"));
        await using var host = await StartHostAsync(Name("billing"), pipeline, Name("order-placed"));
        var message = Message(Name("order-placed"));

        await host.Publisher.PublishAsync(message, Ct);
        await pipeline.WaitForAsync(1, Timeout, Ct);

        var deadLetter = await WaitForDeadLetterAsync(Name("billing"));
        deadLetter.Reason.Should().Be("handler-failed");
        deadLetter.Detail.Should().Be("InvalidOperationException: boom");
        deadLetter.Body.Should().Equal(message.Body.ToArray());
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        pipeline.Deliveries.Should().ContainSingle("a dead-lettered message is not delivered again");
        await AssertSettledAsync(Name("billing"));
    }

    [Fact]
    public async Task CompetingReceiversOnOneQueue_EachMessageIsDeliveredOnce()
    {
        const int messageCount = 40;
        var first = new ScriptedReceivePipeline();
        var second = new ScriptedReceivePipeline();
        await using var firstHost = await StartHostAsync(Name("billing"), first, Name("order-placed"));
        await using var secondHost = await StartHostAsync(Name("billing"), second, Name("order-placed"));

        var published = Enumerable.Range(0, messageCount).Select(_ => Message(Name("order-placed"))).ToList();
        foreach (var message in published)
        {
            await firstHost.Publisher.PublishAsync(message, Ct);
        }

        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (first.Deliveries.Count + second.Deliveries.Count < messageCount && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
        }

        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        var ids = first.Deliveries.Concat(second.Deliveries).Select(d => d.Message.Headers[BrokerHeaders.MessageId]).ToList();
        ids.Should().HaveCount(messageCount, "every message is delivered, and only once").And.OnlyHaveUniqueItems();
        ids.Should().BeEquivalentTo(published.Select(m => m.Headers[BrokerHeaders.MessageId]));
    }

    /// <summary>Registers the transport under test for <paramref name="topology"/>.</summary>
    /// <param name="services">The host's services (logging and the pipeline are already registered).</param>
    /// <param name="settings">The bound <c>MessageBus</c> settings.</param>
    /// <param name="configuration">The configuration the transport reads its own keys from.</param>
    /// <param name="topology">The host's queue and subscriptions.</param>
    protected abstract void RegisterTransport(IServiceCollection services, MessageBusSettings settings, IConfiguration configuration, BrokerTopology topology);

    /// <summary>Reads what the transport dead-lettered from <paramref name="queue"/> so far.</summary>
    /// <param name="queue">The queue name.</param>
    /// <returns>The dead letters.</returns>
    protected abstract Task<IReadOnlyList<DeadLetteredMessage>> ReadDeadLettersAsync(string queue);

    /// <summary>
    /// Waits until <paramref name="queue"/> is bound to every one of <paramref name="topics"/> and
    /// <paramref name="receivers"/> receivers consume it, so a publish that follows is not lost.
    /// </summary>
    /// <param name="queue">The queue name.</param>
    /// <param name="topics">The topics the queue consumes.</param>
    /// <param name="receivers">How many started receivers consume the queue.</param>
    /// <returns>A task that completes when the queue is ready.</returns>
    protected abstract Task WaitUntilReceivingAsync(string queue, IReadOnlyList<string> topics, int receivers);

    /// <summary>Asserts nothing of <paramref name="queue"/> is left unsettled on the broker.</summary>
    /// <param name="queue">The queue name.</param>
    /// <returns>A task that completes when the assertion passed.</returns>
    protected abstract Task AssertSettledAsync(string queue);

    /// <summary>A name unique to this test.</summary>
    /// <param name="name">The base name.</param>
    /// <returns>The unique name.</returns>
    protected string Name(string name) => $"{name}-{_suffix}";

    private static OutgoingBrokerMessage Message(string topic)
    {
        var messageId = Guid.NewGuid();
        return new OutgoingBrokerMessage(
            topic,
            "order-placed",
            messageId,
            System.Text.Encoding.UTF8.GetBytes("{\"orderId\":\"" + messageId.ToString("D", CultureInfo.InvariantCulture) + "\"}"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [BrokerHeaders.EventName] = "order-placed",
                [BrokerHeaders.MessageId] = messageId.ToString("D", CultureInfo.InvariantCulture),
                [BrokerHeaders.ContentType] = BrokerHeaders.JsonContentType,
                [BrokerHeaders.TraceParent] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
                ["MMCA-UserId"] = "42",
                ["MMCA-TenantId"] = "Tenant-A",
            });
    }

    private async Task<DeadLetteredMessage> WaitForDeadLetterAsync(string queue)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (true)
        {
            var deadLetters = await ReadDeadLettersAsync(queue);
            if (deadLetters.Count > 0)
            {
                return deadLetters.Should().ContainSingle().Subject;
            }

            DateTimeOffset.UtcNow.Should().BeBefore(deadline, "the dead letter should have arrived");
            await Task.Delay(100, Ct);
        }
    }

    private async Task<TransportHost> StartHostAsync(string queue, IBrokerReceivePipeline pipeline, params string[] topics)
    {
        var settings = new MessageBusSettings { ConcurrentMessageLimit = 4 };
        var configuration = new ConfigurationBuilder().Build();
        var topology = new BrokerTopology(
            "demo",
            queue,
            [.. topics.Select(topic => new BrokerSubscription(topic, typeof(object), topic, BrokerConsumerKind.Current))]);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(pipeline);
        RegisterTransport(services, settings, configuration, topology);

        var host = new TransportHost(services.BuildServiceProvider());
        await host.StartAsync();

        // A receiver binds its queue in the background once started; a publish before that would
        // reach no queue, so wait until this receiver (and any earlier one on the queue) is bound.
        if (topics.Length > 0)
        {
            var receivers = _receivers.TryGetValue(queue, out var running) ? running + 1 : 1;
            _receivers[queue] = receivers;
            await WaitUntilReceivingAsync(queue, topics, receivers);
        }

        return host;
    }

    /// <summary>One dead-lettered message, as the transport stored it.</summary>
    /// <param name="Reason">The dead-letter reason.</param>
    /// <param name="Detail">The dead-letter detail.</param>
    /// <param name="Body">The body.</param>
    protected sealed record DeadLetteredMessage(string Reason, string? Detail, byte[] Body);

    /// <summary>A started host: the provider and its hosted services, stopped in reverse on dispose.</summary>
    private sealed class TransportHost(ServiceProvider provider) : IAsyncDisposable
    {
        private readonly List<IHostedService> _started = [];

        public IBrokerPublisher Publisher => provider.GetRequiredService<IBrokerPublisher>();

        public async Task StartAsync()
        {
            foreach (var service in provider.GetServices<IHostedService>())
            {
                await service.StartAsync(Ct);
                _started.Add(service);
            }
        }

        public async ValueTask DisposeAsync()
        {
            for (var i = _started.Count - 1; i >= 0; i--)
            {
                await _started[i].StopAsync(CancellationToken.None);
            }

            await provider.DisposeAsync();
        }
    }
}

using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Infrastructure.Messaging.Transports;
using MMCA.Common.Infrastructure.Messaging.Transports.RabbitMq;
using MMCA.Common.Tests.Shared.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace MMCA.Common.Infrastructure.RabbitMq.Tests;

/// <summary>
/// The built-in RabbitMQ broker transport against a REAL RabbitMQ: the shared transport conformance
/// scenarios (round trip, fan-out, delayed redelivery through a TTL delay queue with no plugin, dead
/// letter to <c>{queue}-error</c>, competing consumers) plus the entities a host declares and the
/// publisher's recovery from a channel the broker closed.
/// <para>
/// These tests need a Docker daemon, so this project is outside <c>MMCA.Common.slnx</c>.
/// </para>
/// </summary>
public sealed class RabbitMqBrokerTransportTests(RabbitMqBrokerFixture fixture)
    : BrokerTransportConformanceTestsBase, IClassFixture<RabbitMqBrokerFixture>
{
    [Fact]
    public async Task Receiver_DeclaresTheQueueAndItsErrorQueue_AndARedeliveryDeclaresItsDelayQueue()
    {
        var pipeline = new ScriptedReceivePipeline((_, attempt) =>
            attempt == 1 ? new BrokerDisposition.Redeliver(TimeSpan.FromSeconds(1), 1) : BrokerDisposition.Complete);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IBrokerReceivePipeline>(pipeline);
        var queue = Name("billing");
        RegisterTransport(services, new MessageBusSettings(), new ConfigurationBuilder().Build(), new BrokerTopology("demo", queue, [new BrokerSubscription("e", typeof(object), Name("order-placed"), BrokerConsumerKind.Current)]));
        await using var provider = services.BuildServiceProvider();
        var receiver = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().Single();
        await receiver.StartAsync(Ct);
        try
        {
            await WaitUntilReceivingAsync(queue, [Name("order-placed")], 1);
            (await InspectAsync(queue + "-error")).Should().NotBeNull("the error queue is provisioned with the host queue");
            (await InspectAsync(queue + "-delay-1s")).Should().BeNull("a delay queue is declared lazily, on first use");

            // The message id header is what the scripted pipeline counts attempts by.
            var messageId = Guid.NewGuid();
            await provider.GetRequiredService<IBrokerPublisher>().PublishAsync(
                new OutgoingBrokerMessage(
                    Name("order-placed"),
                    "e",
                    messageId,
                    new byte[] { 1 },
                    new Dictionary<string, string> { [BrokerHeaders.MessageId] = messageId.ToString("D") }),
                Ct);
            var deliveries = await pipeline.WaitForAsync(2, Timeout, Ct);

            deliveries[1].Message.RedeliveryCount.Should().Be(1, "the second delivery is the copy from the delay queue");
            (deliveries[1].ReceivedAt - deliveries[0].ReceivedAt).Should().BeGreaterThan(TimeSpan.FromMilliseconds(500));
            (await InspectAsync(queue + "-delay-1s")).Should().NotBeNull("the first redelivery at that interval declared it");
            await AssertSettledAsync(queue + "-delay-1s");
        }
        finally
        {
            await receiver.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Publisher_NotProvisioning_ToAMissingExchange_Throws_ThenRecoversOnANewChannel()
    {
        var uri = new Uri(fixture.ConnectionString);
        await using var publisher = new RabbitMqPublisher(
            new RabbitMqChannelOperations(new RabbitMqConnectionSource(uri, "probe (publish)"), ownsConnections: true),
            new RabbitMqProvisioner(provisionTopology: false));
        var missing = new OutgoingBrokerMessage(Name("missing"), "e", Guid.NewGuid(), new byte[] { 1 }, new Dictionary<string, string>());

        var act = () => publisher.PublishAsync(missing, Ct);
        await act.Should().ThrowAsync<Exception>("a publish the broker refused must surface to the outbox");

        await using (var channel = await fixture.Probe.CreateChannelAsync(cancellationToken: Ct))
        {
            await channel.ExchangeDeclareAsync(Name("present"), ExchangeType.Fanout, durable: true, cancellationToken: Ct);
        }

        var present = missing with { Topic = Name("present") };
        await publisher.Invoking(p => p.PublishAsync(present, Ct)).Should().NotThrowAsync("the closed channel is replaced");
    }

    protected override void RegisterTransport(IServiceCollection services, MessageBusSettings settings, IConfiguration configuration, BrokerTopology topology) =>
        new RabbitMqBrokerTransport().Register(
            services,
            new BrokerTransportContext(settings, fixture.ConnectionString, configuration, topology));

    protected override async Task WaitUntilReceivingAsync(string queue, IReadOnlyList<string> topics, int receivers)
    {
        // The receiver binds every exchange before it starts consuming, so a consumer on the queue means
        // the bindings are in place.
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while ((await InspectAsync(queue))?.ConsumerCount is not { } consumers || consumers < receivers)
        {
            DateTimeOffset.UtcNow.Should().BeBefore(deadline, "the receiver should consume its queue");
            await Task.Delay(50, Ct);
        }
    }

    protected override async Task AssertSettledAsync(string queue)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while ((await InspectAsync(queue))?.MessageCount is not 0)
        {
            DateTimeOffset.UtcNow.Should().BeBefore(deadline, "the queue should be drained");
            await Task.Delay(50, Ct);
        }
    }

    protected override async Task<IReadOnlyList<DeadLetteredMessage>> ReadDeadLettersAsync(string queue)
    {
        await using var channel = await fixture.Probe.CreateChannelAsync(cancellationToken: Ct);
        List<DeadLetteredMessage> deadLetters = [];
        while (await channel.BasicGetAsync(queue + "-error", autoAck: true, Ct) is { } result)
        {
            var headers = result.BasicProperties.Headers ?? new Dictionary<string, object?>();
            deadLetters.Add(new DeadLetteredMessage(
                Encoding.UTF8.GetString((byte[])headers[RabbitMqNames.DeadLetterReasonHeader]!),
                headers.TryGetValue(RabbitMqNames.DeadLetterDetailHeader, out var detail) ? Encoding.UTF8.GetString((byte[])detail!) : null,
                result.Body.ToArray()));
        }

        return deadLetters;
    }

    private async Task<QueueDeclareOk?> InspectAsync(string queue)
    {
        // A passive declaration of a missing queue closes its channel, so each probe gets its own.
        var channel = await fixture.Probe.CreateChannelAsync(cancellationToken: Ct);
        try
        {
            return await channel.QueueDeclarePassiveAsync(queue, Ct);
        }
        catch (OperationInterruptedException)
        {
            return null;
        }
        finally
        {
            await channel.DisposeAsync();
        }
    }
}

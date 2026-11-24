using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Infrastructure;
using MMCA.Common.Infrastructure.Context;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Messaging;
using SqsMessageAttributeValue = Amazon.SQS.Model.MessageAttributeValue;

namespace MMCA.Common.Aws.LocalStack.Tests;

/// <summary>A probe event for the header round trip.</summary>
public sealed record class AwsHeaderRoundTripEvent : BaseIntegrationEvent;

/// <summary>A probe event consumed by two hosts.</summary>
public sealed record class AwsFanOutEvent(Guid Marker) : BaseIntegrationEvent;

/// <summary>A probe event whose handler fails the first delivery.</summary>
public sealed record class AwsRedeliveryEvent : BaseIntegrationEvent;

/// <summary>A probe event whose handler always fails.</summary>
public sealed record class AwsDoomedEvent : BaseIntegrationEvent;

/// <summary>A probe event published but consumed by nobody.</summary>
public sealed record class AwsPublishOnlyEvent : BaseIntegrationEvent;

/// <summary>
/// The native SQS/SNS transport, registered through <c>AddAmazonSqsTransport()</c> +
/// <c>AddBrokerMessaging</c> exactly as a host would, against LocalStack: the topology it provisions
/// (SNS topic per event, the queue subscribed with raw delivery, the queue policy, the error queue), the
/// publish to consume round trip with every ambient header packed into one attribute and restored on
/// the far side, fan-out to two queues, a delayed second-level redelivery, dead-lettering into the
/// <c>{queue}-error</c> queue (handler failure and an unknown event), and the publisher creating a
/// missing topic on its first publish. Each test runs its own host on its own namespace and queue.
/// </summary>
public sealed class AmazonSqsTransportLocalStackTests(LocalStackFixture localStack)
{
    private const string ExpectedTenantId = "tenant-aws";
    private const string ExpectedCorrelationId = "correlation-aws-1";
    private const string ExpectedUserId = "42";

    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(90);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Topology_IsATopicPerEvent_TheQueueSubscribedRaw_ThePolicy_AndTheErrorQueue()
    {
        var name = NewName("topo");
        await using var host = await StartHostAsync(name, name, ["0"], services => services.AddSingleton<IIntegrationEventHandler<AwsHeaderRoundTripEvent>>(new ContextRecordingHandler<AwsHeaderRoundTripEvent>(0)), c => c.RegisterIntegrationEventConsumer<AwsHeaderRoundTripEvent>());

        using var sqs = localStack.CreateSqsClient();
        using var sns = localStack.CreateSnsClient();
        var queueUrl = (await sqs.GetQueueUrlAsync(name, Token)).QueueUrl;
        (await sqs.GetQueueUrlAsync(name + "-error", Token)).QueueUrl.Should().NotBeNullOrEmpty();

        var attributes = await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["All"] }, Token);
        var topicArn = (await sns.FindTopicAsync(TopicOf<AwsHeaderRoundTripEvent>(name))).TopicArn;
        attributes.Policy.Should().Contain(topicArn).And.Contain("sqs:SendMessage").And.Contain("sns.amazonaws.com");
        attributes.VisibilityTimeout.Should().Be(60);

        var subscription = (await sns.ListSubscriptionsByTopicAsync(topicArn, Token)).Subscriptions
            .Should().ContainSingle(s => s.Endpoint == attributes.QueueARN).Subject;
        var subscriptionAttributes = await sns.GetSubscriptionAttributesAsync(subscription.SubscriptionArn, Token);
        subscriptionAttributes.Attributes["RawMessageDelivery"].Should().Be("true");
    }

    [Fact]
    public async Task APublishedEvent_CarriesEveryAmbientHeaderInOneAttribute_AndTheConsumerRestoresTheContext()
    {
        var name = NewName("hdr");
        var handler = new ContextRecordingHandler<AwsHeaderRoundTripEvent>(failFirstAttempts: 0);
        await using var host = await StartHostAsync(name, name, ["0"], services => services.AddSingleton<IIntegrationEventHandler<AwsHeaderRoundTripEvent>>(handler), c => c.RegisterIntegrationEventConsumer<AwsHeaderRoundTripEvent>());

        // A second, raw-subscribed probe queue on the same topic shows what actually crosses the wire.
        using var sqs = localStack.CreateSqsClient();
        using var sns = localStack.CreateSnsClient();
        var probeUrl = await SubscribeProbeQueueAsync(sqs, sns, name + "-probe", TopicOf<AwsHeaderRoundTripEvent>(name));

        var published = new AwsHeaderRoundTripEvent();
        await CreatePublisher(host).PublishAsync(published, Token);

        var restored = await handler.Handled.Task.WaitAsync(DeliveryTimeout, Token);
        restored.MessageId.Should().Be(published.MessageId);
        restored.TenantId.Should().Be(ExpectedTenantId, "the consumer restores the tenant before the handler runs");
        restored.CorrelationId.Should().Be(ExpectedCorrelationId);

        var raw = await ReceiveAsync(sqs, probeUrl, _ => true);
        raw.Should().NotBeNull();
        raw!.MessageAttributes.Keys.Should().Equal(["mmca-headers"], "every header travels packed in one attribute, far below the cap of 10");
        var headers = Unpack(raw);
        headers.Count.Should().BeGreaterThanOrEqualTo(8);
        headers[BrokerHeaders.EventName].Should().Be(BrokerNaming.EventName(typeof(AwsHeaderRoundTripEvent)));
        headers[BrokerHeaders.MessageId].Should().Be(published.MessageId.ToString("D"));
        headers[MessageHeaders.TenantId].Should().Be(ExpectedTenantId);
        headers[MessageHeaders.UserId].Should().Be(ExpectedUserId);
        headers[MessageHeaders.UserRoles].Should().Be("Admin,Manager");
        headers[MessageHeaders.UserPermissions].Should().Be("[\"orders.read\",\"orders.write\"]");
        headers[MessageHeaders.CorrelationId].Should().Be(ExpectedCorrelationId);
        raw.Body.Should().Contain(published.MessageId.ToString("D"), "the body is the event's JSON text");
    }

    [Fact]
    public async Task OnePublish_FansOutToEveryQueueSubscribedToTheTopic()
    {
        var ns = NewName("fan");
        var first = new ConcurrentBag<Guid>();
        var second = new ConcurrentBag<Guid>();
        await using var hostA = await StartHostAsync(ns, ns + "-a", ["0"], s => s.AddSingleton<IIntegrationEventHandler<AwsFanOutEvent>>(new RecordingHandler(first)), c => c.RegisterIntegrationEventConsumer<AwsFanOutEvent>());
        await using var hostB = await StartHostAsync(ns, ns + "-b", ["0"], s => s.AddSingleton<IIntegrationEventHandler<AwsFanOutEvent>>(new RecordingHandler(second)), c => c.RegisterIntegrationEventConsumer<AwsFanOutEvent>());

        var marker = Guid.NewGuid();
        await CreatePublisher(hostA).PublishAsync(new AwsFanOutEvent(marker), Token);

        (await WaitUntilAsync(() => first.Contains(marker) && second.Contains(marker))).Should().BeTrue("both queues are subscribed to the one topic");
        await Task.Delay(TimeSpan.FromSeconds(2), Token);
        first.Count(m => m == marker).Should().Be(1);
        second.Count(m => m == marker).Should().Be(1);
    }

    [Fact]
    public async Task AFailedDelivery_IsRedeliveredThroughAnSqsDelay_WithTheHeadersIntact()
    {
        var name = NewName("rdl");
        var handler = new ContextRecordingHandler<AwsRedeliveryEvent>(failFirstAttempts: 1);
        await using var host = await StartHostAsync(name, name, ["3"], services => services.AddSingleton<IIntegrationEventHandler<AwsRedeliveryEvent>>(handler), c => c.RegisterIntegrationEventConsumer<AwsRedeliveryEvent>());

        var published = new AwsRedeliveryEvent();
        var started = DateTimeOffset.UtcNow;
        await CreatePublisher(host).PublishAsync(published, Token);

        var restored = await handler.Handled.Task.WaitAsync(DeliveryTimeout, Token);
        handler.Attempts.Should().Be(2, "the first attempt throws and the second-level redelivery succeeds");
        (DateTimeOffset.UtcNow - started).Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(2), "the copy is sent with DelaySeconds, not at once");
        restored.MessageId.Should().Be(published.MessageId);
        restored.TenantId.Should().Be(ExpectedTenantId, "the redelivered copy still carries the ambient headers");
        restored.CorrelationId.Should().Be(ExpectedCorrelationId);
    }

    [Fact]
    public async Task AnEventThatAlwaysFails_EndsInTheErrorQueue_WithTheReasonAndDetail()
    {
        var name = NewName("dlq");
        var handler = new ContextRecordingHandler<AwsDoomedEvent>(failFirstAttempts: int.MaxValue);
        await using var host = await StartHostAsync(name, name, ["1"], services => services.AddSingleton<IIntegrationEventHandler<AwsDoomedEvent>>(handler), c => c.RegisterIntegrationEventConsumer<AwsDoomedEvent>());

        var published = new AwsDoomedEvent();
        await CreatePublisher(host).PublishAsync(published, Token);

        using var sqs = localStack.CreateSqsClient();
        var errorUrl = (await sqs.GetQueueUrlAsync(name + "-error", Token)).QueueUrl;
        var deadLettered = await ReceiveAsync(sqs, errorUrl, m => Unpack(m).GetValueOrDefault(BrokerHeaders.MessageId) == published.MessageId.ToString("D"));

        deadLettered.Should().NotBeNull();
        var headers = Unpack(deadLettered!);
        headers["mmca-dead-letter-reason"].Should().Be("handler-failed");
        headers["mmca-dead-letter-detail"].Should().Contain("Simulated handler failure.");
        headers[BrokerHeaders.RedeliveryCount].Should().Be("1", "the dead-lettered message is the redelivered copy");
        headers[MessageHeaders.TenantId].Should().Be(ExpectedTenantId);
        handler.Attempts.Should().Be(2, "one delivery plus the one configured redelivery");
        (await ReceiveAsync(sqs, (await sqs.GetQueueUrlAsync(name, Token)).QueueUrl, _ => true, TimeSpan.FromSeconds(3)))
            .Should().BeNull("the original is deleted from the host's queue");
    }

    [Fact]
    public async Task AnUnknownEvent_IsDeadLetteredWithoutRetry()
    {
        var name = NewName("unk");
        await using var host = await StartHostAsync(name, name, ["1"], services => services.AddSingleton<IIntegrationEventHandler<AwsHeaderRoundTripEvent>>(new ContextRecordingHandler<AwsHeaderRoundTripEvent>(0)), c => c.RegisterIntegrationEventConsumer<AwsHeaderRoundTripEvent>());

        using var sqs = localStack.CreateSqsClient();
        var queueUrl = (await sqs.GetQueueUrlAsync(name, Token)).QueueUrl;
        var messageId = Guid.NewGuid().ToString("D");
        await sqs.SendMessageAsync(
            new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = "{}",
                MessageAttributes = new Dictionary<string, SqsMessageAttributeValue>
                {
                    ["mmca-headers"] = new()
                    {
                        DataType = "String",
                        StringValue = $"{{\"{BrokerHeaders.EventName}\":\"nobody-consumes-this\",\"{BrokerHeaders.MessageId}\":\"{messageId}\"}}",
                    },
                },
            },
            Token);

        var errorUrl = (await sqs.GetQueueUrlAsync(name + "-error", Token)).QueueUrl;
        var deadLettered = await ReceiveAsync(sqs, errorUrl, m => Unpack(m).GetValueOrDefault(BrokerHeaders.MessageId) == messageId);

        deadLettered.Should().NotBeNull();
        Unpack(deadLettered!)["mmca-dead-letter-reason"].Should().Be("unknown-event");
        Unpack(deadLettered!).Should().NotContainKey(BrokerHeaders.RedeliveryCount, "an unknown event is never redelivered");
    }

    [Fact]
    public async Task ThePublisher_CreatesAMissingTopicOnItsFirstPublish()
    {
        var name = NewName("pub");
        await using var host = await StartHostAsync(name, name, ["0"], _ => { }, _ => { });
        using var sns = localStack.CreateSnsClient();
        var topic = TopicOf<AwsPublishOnlyEvent>(name);
        (await sns.FindTopicAsync(topic)).Should().BeNull("nothing consumes the event, so nothing provisioned its topic");

        await CreatePublisher(host).PublishAsync(new AwsPublishOnlyEvent(), Token);

        (await sns.FindTopicAsync(topic)).Should().NotBeNull();
    }

    private static string NewName(string kind) => "it-" + kind + "-" + Guid.NewGuid().ToString("N")[..8];

    private static string TopicOf<TEvent>(string applicationNamespace) =>
        BrokerNaming.Fit(BrokerNaming.Topic(applicationNamespace, BrokerNaming.EventName(typeof(TEvent))), 256);

    private static Dictionary<string, string> Unpack(Message message)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (message.MessageAttributes?.TryGetValue("mmca-headers", out var packed) != true)
        {
            return headers;
        }

        using var document = System.Text.Json.JsonDocument.Parse(packed!.StringValue);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            headers[property.Name] = property.Value.GetString()!;
        }

        return headers;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + DeliveryTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(250, Token);
        }

        return condition();
    }

    private static async Task<Message?> ReceiveAsync(AmazonSQSClient sqs, string queueUrl, Func<Message, bool> match, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DeliveryTimeout);
        while (DateTime.UtcNow < deadline)
        {
            var response = await sqs.ReceiveMessageAsync(
                new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 1, MessageAttributeNames = ["All"] },
                Token);
            var hit = (response.Messages ?? []).FirstOrDefault(match);
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    private static async Task<string> SubscribeProbeQueueAsync(AmazonSQSClient sqs, AmazonSimpleNotificationServiceClient sns, string queueName, string topic)
    {
        var queueUrl = (await sqs.CreateQueueAsync(queueName, Token)).QueueUrl;
        var queueArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["QueueArn"] }, Token)).QueueARN;
        var topicArn = (await sns.FindTopicAsync(topic)).TopicArn;
        await sns.SubscribeAsync(
            new SubscribeRequest
            {
                TopicArn = topicArn,
                Protocol = "sqs",
                Endpoint = queueArn,
                Attributes = new Dictionary<string, string> { ["RawMessageDelivery"] = "true" },
            },
            Token);
        return queueUrl;
    }

    /// <summary>
    /// <c>BrokerMessageBus</c> over the host's SNS publisher, carrying the ambient context of an
    /// authenticated request, the state the outbox processor restores onto its scope before it
    /// publishes a row.
    /// </summary>
    private static BrokerMessageBus CreatePublisher(RunningHost host)
    {
        var tenant = new TenantContext();
        tenant.SetTenant(ExpectedTenantId);
        var correlation = new CorrelationContext();
        correlation.SetCorrelationId(ExpectedCorrelationId);

        return new BrokerMessageBus(
            host.Services.GetRequiredService<IBrokerPublisher>(),
            host.Services.GetRequiredService<BrokerTopology>(),
            new StaticCurrentUser(),
            tenant,
            correlation);
    }

    /// <summary>
    /// The host a service runs: the adapter transport, then <c>AddBrokerMessaging</c>. The inbox is
    /// off (it needs a database this tier does not have) and in-process retries are off, so a failure
    /// goes straight to the SQS-delayed second level. Returns once every consumed topic is subscribed.
    /// </summary>
    private async Task<RunningHost> StartHostAsync(
        string applicationNamespace,
        string queueName,
        string[] redeliveryIntervals,
        Action<IServiceCollection> configureServices,
        Action<IBrokerConsumerBuilder> configureConsumers)
    {
        var settings = localStack.MessageBusSettings(queueName);
        settings["applicationName"] = queueName;
        settings["Application:Namespace"] = applicationNamespace;
        settings["MessageBus:EnableInbox"] = "false";
        settings["MessageBus:RetryLimit"] = "0";
        settings["MessageBus:RetryMinIntervalSeconds"] = "0";
        for (var i = 0; i < redeliveryIntervals.Length; i++)
        {
            settings[string.Create(CultureInfo.InvariantCulture, $"MessageBus:RedeliveryIntervalsSeconds:{i}")] = redeliveryIntervals[i];
        }

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { ApplicationName = queueName });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddLogging();

        // The two ambient carriers AddInfrastructure registers, so the consumer has something to
        // restore onto and a handler's own scope can read the restored values back.
        builder.Services.AddScoped<ITenantContext, TenantContext>();
        builder.Services.AddScoped<ICorrelationContext, CorrelationContext>();
        configureServices(builder.Services);

        builder.Services.AddAmazonSqsTransport();
        builder.Services.AddBrokerMessaging(builder.Configuration, configureConsumers);

        var host = builder.Build();
        await host.StartAsync(Token);

        var topology = host.Services.GetRequiredService<BrokerTopology>();
        if (topology.Subscriptions.Count > 0)
        {
            await WaitForSubscriptionsAsync(topology);
        }

        return new RunningHost(host);
    }

    private async Task WaitForSubscriptionsAsync(BrokerTopology topology)
    {
        using var sqs = localStack.CreateSqsClient();
        using var sns = localStack.CreateSnsClient();
        var deadline = DateTime.UtcNow + DeliveryTimeout;
        foreach (var topic in topology.Subscriptions.Select(s => BrokerNaming.Fit(s.Topic, 256)).Distinct(StringComparer.Ordinal))
        {
            while (true)
            {
                DateTime.UtcNow.Should().BeBefore(deadline, "the receiver provisions the topology in the background");
                var topicArn = (await sns.FindTopicAsync(topic))?.TopicArn;
                if (topicArn is not null
                    && (await sns.ListSubscriptionsByTopicAsync(topicArn, Token)).Subscriptions?.Exists(s => s.Endpoint.EndsWith(":" + topology.QueueName, StringComparison.Ordinal)) == true)
                {
                    break;
                }

                await Task.Delay(250, Token);
            }
        }
    }

    /// <summary>A started host that is stopped (so the receiver drains) before it is disposed.</summary>
    private sealed class RunningHost(IHost host) : IAsyncDisposable
    {
        public IServiceProvider Services => host.Services;

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
        }
    }

    /// <summary>An authenticated user with two roles and two permission claims.</summary>
    private sealed class StaticCurrentUser : ICurrentUserService
    {
        public ClaimsPrincipal User { get; } = new(new ClaimsIdentity(
            [
                new Claim(AuthClaimTypes.Subject, ExpectedUserId),
                new Claim(ClaimTypes.Role, "Admin"),
                new Claim(ClaimTypes.Role, "Manager"),
                new Claim(AuthClaimTypes.Permission, "orders.read"),
                new Claim(AuthClaimTypes.Permission, "orders.write"),
            ],
            authenticationType: "Test"));

        public UserIdentifierType? UserId => 42;

        public string? Role => "Admin";

        public T? GetClaimValue<T>(string claimType)
            where T : struct, IParsable<T> => null;
    }

    /// <summary>Records each fan-out marker it handles.</summary>
    private sealed class RecordingHandler(ConcurrentBag<Guid> received) : IIntegrationEventHandler<AwsFanOutEvent>
    {
        public Task HandleAsync(AwsFanOutEvent integrationEvent, CancellationToken cancellationToken = default)
        {
            received.Add(integrationEvent.Marker);
            return Task.CompletedTask;
        }
    }

    /// <summary>What a handler saw through freshly constructed ambient carriers.</summary>
    public sealed record RestoredContext(Guid MessageId, string? TenantId, string? CorrelationId);

    /// <summary>
    /// A singleton handler that records the tenant and correlation id a NEW <c>TenantContext</c> /
    /// <c>CorrelationContext</c> sees, which is exactly what a scope opened from the root provider would
    /// construct: both seed themselves from the origin the consumer restored for this hop. Optionally
    /// throws on its first attempts to drive redelivery.
    /// </summary>
    /// <typeparam name="TEvent">The integration event handled.</typeparam>
    public sealed class ContextRecordingHandler<TEvent>(int failFirstAttempts) : IIntegrationEventHandler<TEvent>
        where TEvent : BaseIntegrationEvent
    {
        private int _attempts;

        public TaskCompletionSource<RestoredContext> Handled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Attempts => Volatile.Read(ref _attempts);

        public Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(integrationEvent);

            if (Interlocked.Increment(ref _attempts) <= failFirstAttempts)
            {
                throw new InvalidOperationException("Simulated handler failure.");
            }

            var tenant = new TenantContext();
            var correlation = new CorrelationContext();
            Handled.TrySetResult(new RestoredContext(integrationEvent.MessageId, tenant.TenantId, correlation.CorrelationId));
            return Task.CompletedTask;
        }
    }
}

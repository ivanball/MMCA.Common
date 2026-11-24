using System.Text.Json;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Aws.Messaging;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Broker;
using Moq;

namespace MMCA.Common.Aws.Tests.Messaging;

/// <summary>
/// The entities the transport derives and creates: name fitting, the queue policy document, the queue
/// attributes, the receive request and backpressure, and the provisioner's calls against mocked SQS and
/// SNS clients (provisioning on and off, the topic ARN cache, and an existing non-raw subscription).
/// </summary>
public sealed class AmazonSqsTopologyTests
{
    private const string QueueUrl = "https://sqs.us-east-1.amazonaws.com/000000000000/orders";
    private const string ErrorQueueUrl = "https://sqs.us-east-1.amazonaws.com/000000000000/orders-error";
    private const string QueueArn = "arn:aws:sqs:us-east-1:000000000000:orders";

    private static BrokerTopology TopologyConsuming(params string[] eventNames) =>
        new("shop", "orders", [.. eventNames.Select(e => new BrokerSubscription(e, typeof(object), BrokerNaming.Topic("shop", e), BrokerConsumerKind.Current))]);

    private static string ArnFor(string topic) => "arn:aws:sns:us-east-1:000000000000:" + topic;

    [Fact]
    public void Names_ThatFit_AreUnchanged()
    {
        AmazonSqsEntityNames.Topic("shop-order-placed").Should().Be("shop-order-placed");
        AmazonSqsEntityNames.Queue("orders").Should().Be("orders");
        AmazonSqsEntityNames.ErrorQueue("orders").Should().Be("orders-error");
    }

    [Fact]
    public void Names_ThatDoNotFit_AreShortenedDeterministically_ToTheServiceLimits()
    {
        var longQueue = new string('q', 120);
        var longTopic = new string('t', 300);

        AmazonSqsEntityNames.Queue(longQueue).Should().HaveLength(80).And.Be(AmazonSqsEntityNames.Queue(longQueue));
        AmazonSqsEntityNames.ErrorQueue(longQueue).Should().HaveLength(80).And.NotBe(AmazonSqsEntityNames.Queue(longQueue));
        AmazonSqsEntityNames.Topic(longTopic).Should().HaveLength(256).And.Be(BrokerNaming.Fit(longTopic, 256));
        AmazonSqsEntityNames.ErrorQueue(new string('q', 76)).Should().HaveLength(80, "a 76-character queue plus '-error' needs fitting");
    }

    [Fact]
    public void QueuePolicy_AllowsSnsToSend_FromExactlyTheGivenTopics()
    {
        var topics = new[] { ArnFor("shop-a"), ArnFor("shop-b") };

        using var policy = JsonDocument.Parse(AmazonSqsTopologyProvisioner.BuildQueuePolicy(QueueArn, topics));

        policy.RootElement.GetProperty("Version").GetString().Should().Be("2012-10-17");
        var statement = policy.RootElement.GetProperty("Statement").EnumerateArray().Should().ContainSingle().Subject;
        statement.GetProperty("Effect").GetString().Should().Be("Allow");
        statement.GetProperty("Principal").GetProperty("Service").GetString().Should().Be("sns.amazonaws.com");
        statement.GetProperty("Action").GetString().Should().Be("sqs:SendMessage");
        statement.GetProperty("Resource").GetString().Should().Be(QueueArn);
        statement.GetProperty("Condition").GetProperty("ArnEquals").GetProperty("aws:SourceArn").EnumerateArray()
            .Select(e => e.GetString()).Should().Equal(topics);
    }

    [Fact]
    public void QueueAttributes_SetTheHeartbeatVisibility_AndTheErrorQueueKeepsMessages14Days()
    {
        AmazonSqsTopologyProvisioner.QueueAttributes().Should().Equal(new Dictionary<string, string> { ["VisibilityTimeout"] = "60" });
        AmazonSqsTopologyProvisioner.ErrorQueueAttributes().Should().Equal(new Dictionary<string, string> { ["MessageRetentionPeriod"] = "1209600" });
    }

    [Fact]
    public void ReceiveRequest_LongPolls_WithEveryAttribute_AndTheHeartbeatVisibility()
    {
        var request = AmazonSqsReceiver.BuildReceiveRequest(QueueUrl, 7);

        request.QueueUrl.Should().Be(QueueUrl);
        request.MaxNumberOfMessages.Should().Be(7);
        request.WaitTimeSeconds.Should().Be(20);
        request.VisibilityTimeout.Should().Be(AmazonSqsDeliveryHandler.VisibilityTimeoutSeconds);
        request.MessageAttributeNames.Should().Equal("All");
    }

    [Theory]
    [InlineData(null, 10)]
    [InlineData(0, 10)]
    [InlineData(4, 4)]
    [InlineData(50, 10)]
    public void BatchSize_IsThePrefetchCount_AtMostTen(int? prefetch, int expected) =>
        AmazonSqsReceiver.ResolveBatchSize(new MessageBusSettings { PrefetchCount = prefetch }).Should().Be(expected);

    [Theory]
    [InlineData(null, 16)]
    [InlineData(0, 16)]
    [InlineData(3, 3)]
    public void Concurrency_IsTheConcurrentMessageLimit_Else16(int? limit, int expected) =>
        AmazonSqsReceiver.ResolveConcurrency(new MessageBusSettings { ConcurrentMessageLimit = limit }).Should().Be(expected);

    [Fact]
    public async Task Provisioning_CreatesBothQueues_EachTopicOnce_ThePolicy_AndARawSubscriptionPerTopic()
    {
        var sqs = new Mock<IAmazonSQS>();
        var sns = new Mock<IAmazonSimpleNotificationService>();
        var created = new List<CreateQueueRequest>();
        sqs.Setup(c => c.CreateQueueAsync(It.IsAny<CreateQueueRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CreateQueueRequest, CancellationToken>((r, _) => created.Add(r))
            .ReturnsAsync((CreateQueueRequest r, CancellationToken _) => new CreateQueueResponse { QueueUrl = r.QueueName == "orders" ? QueueUrl : ErrorQueueUrl });
        sqs.Setup(c => c.GetQueueAttributesAsync(It.IsAny<GetQueueAttributesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetQueueAttributesResponse { Attributes = new Dictionary<string, string> { ["QueueArn"] = QueueArn } });
        SetQueueAttributesRequest? policyRequest = null;
        sqs.Setup(c => c.SetQueueAttributesAsync(It.IsAny<SetQueueAttributesRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SetQueueAttributesRequest, CancellationToken>((r, _) => policyRequest = r)
            .ReturnsAsync(new SetQueueAttributesResponse());
        sns.Setup(c => c.CreateTopicAsync(It.IsAny<CreateTopicRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateTopicRequest r, CancellationToken _) => new CreateTopicResponse { TopicArn = ArnFor(r.Name) });
        var subscriptions = new List<SubscribeRequest>();
        sns.Setup(c => c.SubscribeAsync(It.IsAny<SubscribeRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SubscribeRequest, CancellationToken>((r, _) => subscriptions.Add(r))
            .ReturnsAsync(new SubscribeResponse());
        var provisioner = new AmazonSqsTopologyProvisioner(sqs.Object, sns.Object, provision: true, NullLogger.Instance);

        var queues = await provisioner.ProvisionAsync(TopologyConsuming("a", "b"), TestContext.Current.CancellationToken);

        queues.Should().Be((QueueUrl, ErrorQueueUrl));
        created.Select(r => r.QueueName).Should().Equal("orders-error", "orders");
        created[0].Attributes.Should().ContainKey("MessageRetentionPeriod");
        created[1].Attributes["VisibilityTimeout"].Should().Be("60");
        sns.Verify(c => c.CreateTopicAsync(It.IsAny<CreateTopicRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        policyRequest!.QueueUrl.Should().Be(QueueUrl);
        policyRequest.Attributes["Policy"].Should().Be(AmazonSqsTopologyProvisioner.BuildQueuePolicy(QueueArn, [ArnFor("shop-a"), ArnFor("shop-b")]));
        subscriptions.Select(s => s.TopicArn).Should().Equal(ArnFor("shop-a"), ArnFor("shop-b"));
        subscriptions.Should().OnlyContain(s => s.Protocol == "sqs" && s.Endpoint == QueueArn && s.Attributes["RawMessageDelivery"] == "true");
    }

    [Fact]
    public async Task Provisioning_AnExistingQueueWithOtherAttributes_IsUsedAsIs()
    {
        var sqs = new Mock<IAmazonSQS>();
        var sns = new Mock<IAmazonSimpleNotificationService>();
        sqs.Setup(c => c.CreateQueueAsync(It.IsAny<CreateQueueRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new QueueNameExistsException("exists"));
        sqs.Setup(c => c.GetQueueUrlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, CancellationToken _) => new GetQueueUrlResponse { QueueUrl = name == "orders" ? QueueUrl : ErrorQueueUrl });
        sqs.Setup(c => c.GetQueueAttributesAsync(It.IsAny<GetQueueAttributesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetQueueAttributesResponse { Attributes = new Dictionary<string, string> { ["QueueArn"] = QueueArn } });
        sqs.Setup(c => c.SetQueueAttributesAsync(It.IsAny<SetQueueAttributesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SetQueueAttributesResponse());
        sns.Setup(c => c.CreateTopicAsync(It.IsAny<CreateTopicRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateTopicRequest r, CancellationToken _) => new CreateTopicResponse { TopicArn = ArnFor(r.Name) });
        sns.Setup(c => c.SubscribeAsync(It.IsAny<SubscribeRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new SubscribeResponse());
        var provisioner = new AmazonSqsTopologyProvisioner(sqs.Object, sns.Object, provision: true, NullLogger.Instance);

        var queues = await provisioner.ProvisionAsync(TopologyConsuming("a"), TestContext.Current.CancellationToken);

        queues.Should().Be((QueueUrl, ErrorQueueUrl));
    }

    [Fact]
    public async Task Provisioning_AnExistingNonRawSubscription_IsSwitchedToRawDelivery()
    {
        var sqs = new Mock<IAmazonSQS>();
        var sns = new Mock<IAmazonSimpleNotificationService>();
        sqs.Setup(c => c.CreateQueueAsync(It.IsAny<CreateQueueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateQueueResponse { QueueUrl = QueueUrl });
        sqs.Setup(c => c.GetQueueAttributesAsync(It.IsAny<GetQueueAttributesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetQueueAttributesResponse { Attributes = new Dictionary<string, string> { ["QueueArn"] = QueueArn } });
        sqs.Setup(c => c.SetQueueAttributesAsync(It.IsAny<SetQueueAttributesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SetQueueAttributesResponse());
        sns.Setup(c => c.CreateTopicAsync(It.IsAny<CreateTopicRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateTopicRequest r, CancellationToken _) => new CreateTopicResponse { TopicArn = ArnFor(r.Name) });
        sns.Setup(c => c.SubscribeAsync(It.IsAny<SubscribeRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Amazon.SimpleNotificationService.Model.InvalidParameterException("Subscription already exists with different attributes"));
        sns.Setup(c => c.ListSubscriptionsByTopicAsync(It.IsAny<ListSubscriptionsByTopicRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListSubscriptionsByTopicResponse
            {
                Subscriptions =
                [
                    new Subscription { Protocol = "sqs", Endpoint = "arn:aws:sqs:us-east-1:000000000000:other", SubscriptionArn = "sub-other" },
                    new Subscription { Protocol = "sqs", Endpoint = QueueArn, SubscriptionArn = "sub-orders" },
                ],
            });
        sns.Setup(c => c.SetSubscriptionAttributesAsync("sub-orders", "RawMessageDelivery", "true", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SetSubscriptionAttributesResponse())
            .Verifiable();
        var provisioner = new AmazonSqsTopologyProvisioner(sqs.Object, sns.Object, provision: true, NullLogger.Instance);

        await provisioner.ProvisionAsync(TopologyConsuming("a"), TestContext.Current.CancellationToken);

        sns.Verify();
    }

    [Fact]
    public async Task WithProvisioningOff_TheQueuesAreOnlyLookedUp()
    {
        var sqs = new Mock<IAmazonSQS>(MockBehavior.Strict);
        var sns = new Mock<IAmazonSimpleNotificationService>(MockBehavior.Strict);
        sqs.Setup(c => c.GetQueueUrlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, CancellationToken _) => new GetQueueUrlResponse { QueueUrl = name == "orders" ? QueueUrl : ErrorQueueUrl });
        var provisioner = new AmazonSqsTopologyProvisioner(sqs.Object, sns.Object, provision: false, NullLogger.Instance);

        var queues = await provisioner.ProvisionAsync(TopologyConsuming("a"), TestContext.Current.CancellationToken);

        queues.Should().Be((QueueUrl, ErrorQueueUrl));
    }

    [Fact]
    public async Task TopicArn_IsCreatedOnce_ThenCached()
    {
        var sns = new Mock<IAmazonSimpleNotificationService>();
        sns.Setup(c => c.CreateTopicAsync(It.IsAny<CreateTopicRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateTopicRequest r, CancellationToken _) => new CreateTopicResponse { TopicArn = ArnFor(r.Name) });
        var provisioner = new AmazonSqsTopologyProvisioner(Mock.Of<IAmazonSQS>(), sns.Object, provision: true, NullLogger.Instance);

        (await provisioner.GetTopicArnAsync("shop-a", TestContext.Current.CancellationToken)).Should().Be(ArnFor("shop-a"));
        (await provisioner.GetTopicArnAsync("shop-a", TestContext.Current.CancellationToken)).Should().Be(ArnFor("shop-a"));

        sns.Verify(c => c.CreateTopicAsync(It.Is<CreateTopicRequest>(r => r.Name == "shop-a"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TopicArn_WithProvisioningOff_IsLookedUp_AndAMissingTopicFailsNamingIt()
    {
        var sns = new Mock<IAmazonSimpleNotificationService>(MockBehavior.Strict);
        sns.Setup(c => c.FindTopicAsync("shop-a")).ReturnsAsync(new Topic { TopicArn = ArnFor("shop-a") });
        sns.Setup(c => c.FindTopicAsync("shop-missing")).ReturnsAsync((Topic)null!);
        var provisioner = new AmazonSqsTopologyProvisioner(Mock.Of<IAmazonSQS>(), sns.Object, provision: false, NullLogger.Instance);

        (await provisioner.GetTopicArnAsync("shop-a", TestContext.Current.CancellationToken)).Should().Be(ArnFor("shop-a"));
        var missing = () => provisioner.GetTopicArnAsync("shop-missing", TestContext.Current.CancellationToken);

        await missing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*shop-missing*ProvisionTopology*");
    }

    [Fact]
    public async Task Publisher_PublishesToTheResolvedTopic_WithThePackedHeaders()
    {
        var sns = new Mock<IAmazonSimpleNotificationService>();
        sns.Setup(c => c.CreateTopicAsync(It.IsAny<CreateTopicRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateTopicRequest r, CancellationToken _) => new CreateTopicResponse { TopicArn = ArnFor(r.Name) });
        PublishRequest? published = null;
        sns.Setup(c => c.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PublishRequest, CancellationToken>((r, _) => published = r)
            .ReturnsAsync(new PublishResponse());
        var publisher = new AmazonSqsPublisher(sns.Object, new AmazonSqsTopologyProvisioner(Mock.Of<IAmazonSQS>(), sns.Object, provision: true, NullLogger.Instance));
        var headers = new Dictionary<string, string> { [BrokerHeaders.EventName] = "a" };

        await publisher.PublishAsync(new OutgoingBrokerMessage("shop-a", "a", Guid.NewGuid(), "{}"u8.ToArray(), headers), TestContext.Current.CancellationToken);

        published!.TopicArn.Should().Be(ArnFor("shop-a"));
        published.Message.Should().Be("{}");
        AmazonSqsMessageMapping.UnpackHeaders(published.MessageAttributes[AmazonSqsMessageMapping.HeadersAttribute].StringValue)
            .Should().BeEquivalentTo(headers);
    }

    [Fact]
    public async Task Publisher_WhenSnsRejects_Throws()
    {
        var sns = new Mock<IAmazonSimpleNotificationService>();
        sns.Setup(c => c.CreateTopicAsync(It.IsAny<CreateTopicRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateTopicRequest r, CancellationToken _) => new CreateTopicResponse { TopicArn = ArnFor(r.Name) });
        sns.Setup(c => c.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonSimpleNotificationServiceException("throttled"));
        var publisher = new AmazonSqsPublisher(sns.Object, new AmazonSqsTopologyProvisioner(Mock.Of<IAmazonSQS>(), sns.Object, provision: true, NullLogger.Instance));

        var act = () => publisher.PublishAsync(
            new OutgoingBrokerMessage("shop-a", "a", Guid.NewGuid(), "{}"u8.ToArray(), new Dictionary<string, string>()),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<AmazonSimpleNotificationServiceException>();
    }
}

using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Aws.Notifications;
using MMCA.Common.Infrastructure.Notifications.Push;
using Moq;

namespace MMCA.Common.Aws.Tests.Notifications;

/// <summary>
/// The SNS native push sender (ADR-044) against mocked clients: user-targeted sends resolve each
/// user's endpoints through the registry's <c>UserId</c> index and publish once per endpoint in SNS's
/// per-platform JSON structure; an endpoint SNS reports disabled is removed with its row and the rest
/// still receive (best-effort); a broadcast is one publish to the topic.
/// </summary>
public sealed class SnsNativePushSenderTests
{
    private const string Table = "installations";
    private const string TopicArn = "arn:aws:sns:us-east-1:000000000000:broadcast";

    private readonly Mock<IAmazonDynamoDB> _dynamoDb = new();
    private readonly Mock<IAmazonSimpleNotificationService> _sns = new();
    private readonly List<PublishRequest> _published = [];

    public SnsNativePushSenderTests()
    {
        _dynamoDb.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse());
        _dynamoDb.Setup(x => x.DeleteItemAsync(It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteItemResponse());
        _sns.Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PublishRequest, CancellationToken>((request, _) => _published.Add(request))
            .ReturnsAsync(new PublishResponse { MessageId = "m-1" });
    }

    private SnsNativePushSender CreateSut()
    {
        var options = Options.Create(new SnsNativePushSettings
        {
            InstallationsTable = Table,
            BroadcastTopicArn = TopicArn,
            FcmPlatformApplicationArn = "arn:aws:sns:us-east-1:000000000000:app/GCM/fcm",
        });
        return new SnsNativePushSender(
            new SnsInstallationRegistry(_dynamoDb.Object, _sns.Object, options),
            _sns.Object,
            options,
            NullLogger<SnsNativePushSender>.Instance);
    }

    private static string EndpointArn(string name) => $"arn:aws:sns:us-east-1:000000000000:endpoint/GCM/fcm/{name}";

    private static Dictionary<string, AttributeValue> Row(string installationId, string userId, string endpoint) => new()
    {
        ["InstallationId"] = new() { S = installationId },
        ["UserId"] = new() { S = userId },
        ["Platform"] = new() { S = "fcmv1" },
        ["PushChannel"] = new() { S = $"token-{installationId}" },
        ["EndpointArn"] = new() { S = EndpointArn(endpoint) },
        ["SubscriptionArn"] = new() { S = $"{TopicArn}:sub-{endpoint}" },
    };

    private void SetupUser(string userId, params Dictionary<string, AttributeValue>[] rows) =>
        _dynamoDb.Setup(x => x.QueryAsync(
                It.Is<QueryRequest>(r => r.ExpressionAttributeValues[":user"].S == userId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse { Items = [.. rows] });

    [Fact]
    public void BuildMessage_HasTheExactSnsPerPlatformShape()
    {
        var metadata = new Dictionary<string, string> { ["route"] = "/notifications/7" };

        var json = SnsPushMessage.Build("Hello", "World", metadata);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("default", "GCM", "APNS", "APNS_SANDBOX");
        doc.RootElement.GetProperty("default").GetString().Should().Be("World");
        doc.RootElement.GetProperty("GCM").GetString().Should().Be(
            """{"fcmV1Message":""" + NativePushPayloads.BuildFcmV1Payload("Hello", "World", metadata) + "}");
        doc.RootElement.GetProperty("APNS").GetString().Should().Be(NativePushPayloads.BuildApnsPayload("Hello", "World", metadata));
        doc.RootElement.GetProperty("APNS_SANDBOX").GetString().Should().Be(NativePushPayloads.BuildApnsPayload("Hello", "World", metadata));

        using var gcm = JsonDocument.Parse(doc.RootElement.GetProperty("GCM").GetString()!);
        gcm.RootElement.GetProperty("fcmV1Message").GetProperty("message").GetProperty("notification")
            .GetProperty("title").GetString().Should().Be("Hello");
    }

    [Fact]
    public async Task SendToUsersAsync_PublishesOncePerEndpointOfEachUser_InTheJsonStructure()
    {
        SetupUser("1", Row("a", "1", "a"), Row("b", "1", "b"));
        SetupUser("2", Row("c", "2", "c"));

        await CreateSut().SendToUsersAsync([1, 2], "Hello", "World", cancellationToken: TestContext.Current.CancellationToken);

        _published.Select(p => p.TargetArn).Should().BeEquivalentTo(EndpointArn("a"), EndpointArn("b"), EndpointArn("c"));
        _published.Should().AllSatisfy(p =>
        {
            p.MessageStructure.Should().Be("json");
            p.TopicArn.Should().BeNull();
            p.Message.Should().Be(SnsPushMessage.Build("Hello", "World", null));
        });
        _dynamoDb.Verify(x => x.QueryAsync(
            It.Is<QueryRequest>(r =>
                r.TableName == Table
                && r.IndexName == SnsInstallationRegistry.UserIdIndexName
                && r.KeyConditionExpression == "UserId = :user"),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task SendToUsersAsync_ADisabledEndpointIsRemoved_AndTheOthersStillReceive()
    {
        SetupUser("1", Row("a", "1", "disabled"), Row("b", "1", "healthy"));
        _sns.Setup(x => x.PublishAsync(It.Is<PublishRequest>(r => r.TargetArn == EndpointArn("disabled")), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new EndpointDisabledException("Endpoint is disabled"));

        await CreateSut().SendToUsersAsync([1], "Hello", "World", cancellationToken: TestContext.Current.CancellationToken);

        _published.Should().ContainSingle().Which.TargetArn.Should().Be(EndpointArn("healthy"));
        _sns.Verify(x => x.UnsubscribeAsync(
            It.Is<UnsubscribeRequest>(r => r.SubscriptionArn == $"{TopicArn}:sub-disabled"), It.IsAny<CancellationToken>()), Times.Once);
        _sns.Verify(x => x.DeleteEndpointAsync(
            It.Is<DeleteEndpointRequest>(r => r.EndpointArn == EndpointArn("disabled")), It.IsAny<CancellationToken>()), Times.Once);
        _dynamoDb.Verify(x => x.DeleteItemAsync(
            It.Is<DeleteItemRequest>(r => r.Key["InstallationId"].S == "a" && r.ExpressionAttributeValues[":endpoint"].S == EndpointArn("disabled")),
            It.IsAny<CancellationToken>()), Times.Once);
        _sns.Verify(x => x.DeleteEndpointAsync(
            It.Is<DeleteEndpointRequest>(r => r.EndpointArn == EndpointArn("healthy")), It.IsAny<CancellationToken>()), Times.Never);
    }

    // M260: two installation ids holding one device token share one endpoint; the device is
    // published to once, and a stale shared endpoint removes every row that points at it.
    [Fact]
    public async Task SendToUsersAsync_TwoRowsSharingAnEndpoint_PublishOnce()
    {
        SetupUser("1", Row("i1", "1", "shared"), Row("i2", "1", "shared"));

        await CreateSut().SendToUsersAsync([1], "Hello", "World", cancellationToken: TestContext.Current.CancellationToken);

        _published.Should().ContainSingle().Which.TargetArn.Should().Be(EndpointArn("shared"));
    }

    [Fact]
    public async Task SendToUsersAsync_ADisabledSharedEndpoint_IsRemovedOnce_WithEveryRowPointingAtIt()
    {
        SetupUser("1", Row("i1", "1", "shared"), Row("i2", "1", "shared"));
        _sns.Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new EndpointDisabledException("Endpoint is disabled"));

        await CreateSut().SendToUsersAsync([1], "Hello", "World", cancellationToken: TestContext.Current.CancellationToken);

        _sns.Verify(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        _sns.Verify(x => x.DeleteEndpointAsync(
            It.Is<DeleteEndpointRequest>(r => r.EndpointArn == EndpointArn("shared")), It.IsAny<CancellationToken>()), Times.Once);
        _dynamoDb.Verify(x => x.DeleteItemAsync(It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _dynamoDb.Verify(x => x.DeleteItemAsync(
            It.Is<DeleteItemRequest>(r => r.Key["InstallationId"].S == "i2"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendToUsersAsync_AnEndpointThatNoLongerExists_IsRemovedFromTheRegistry()
    {
        SetupUser("1", Row("a", "1", "gone"));
        _sns.Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Amazon.SimpleNotificationService.Model.NotFoundException("Endpoint does not exist"));

        await CreateSut().SendToUsersAsync([1], "Hello", "World", cancellationToken: TestContext.Current.CancellationToken);

        _dynamoDb.Verify(x => x.DeleteItemAsync(
            It.Is<DeleteItemRequest>(r => r.Key["InstallationId"].S == "a"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendToUsersAsync_AnyOtherFailingEndpoint_DoesNotStopTheRest()
    {
        SetupUser("1", Row("a", "1", "throttled"), Row("b", "1", "healthy"));
        SetupUser("2", Row("c", "2", "other"));
        _sns.Setup(x => x.PublishAsync(It.Is<PublishRequest>(r => r.TargetArn == EndpointArn("throttled")), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonSimpleNotificationServiceException("Rate exceeded"));

        await CreateSut().SendToUsersAsync([1, 2], "Hello", "World", cancellationToken: TestContext.Current.CancellationToken);

        _published.Select(p => p.TargetArn).Should().BeEquivalentTo(EndpointArn("healthy"), EndpointArn("other"));
        _sns.Verify(x => x.DeleteEndpointAsync(It.IsAny<DeleteEndpointRequest>(), It.IsAny<CancellationToken>()), Times.Never,
            "only a disabled or missing endpoint is stale; a transient failure keeps the registration");
    }

    [Fact]
    public async Task SendToUsersAsync_AFailedLookupForOneUser_DoesNotStopTheOthers()
    {
        _dynamoDb.Setup(x => x.QueryAsync(
                It.Is<QueryRequest>(r => r.ExpressionAttributeValues[":user"].S == "1"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonDynamoDBException("boom"));
        SetupUser("2", Row("c", "2", "c"));

        await CreateSut().SendToUsersAsync([1, 2], "Hello", "World", cancellationToken: TestContext.Current.CancellationToken);

        _published.Should().ContainSingle().Which.TargetArn.Should().Be(EndpointArn("c"));
    }

    // L252: the AWS SDK lets transport failures escape unwrapped (HttpRequestException, IOException,
    // a timeout surfaced as an uncalled-for cancellation). Each is one failing lookup or endpoint,
    // so the rest of the recipients still receive.
    [Fact]
    public async Task SendToUsersAsync_ATransportFailureOnOneLookupOrEndpoint_DoesNotStopTheRest()
    {
        _dynamoDb.Setup(x => x.QueryAsync(
                It.Is<QueryRequest>(r => r.ExpressionAttributeValues[":user"].S == "1"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection reset"));
        SetupUser("2", Row("a", "2", "reset"), Row("b", "2", "timeout"), Row("c", "2", "healthy"));
        SetupUser("3", Row("d", "3", "other"));
        _sns.Setup(x => x.PublishAsync(It.Is<PublishRequest>(r => r.TargetArn == EndpointArn("reset")), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("The response ended prematurely"));
        _sns.Setup(x => x.PublishAsync(It.Is<PublishRequest>(r => r.TargetArn == EndpointArn("timeout")), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("The request timed out"));

        await CreateSut().SendToUsersAsync([1, 2, 3], "Hello", "World", cancellationToken: TestContext.Current.CancellationToken);

        _published.Select(p => p.TargetArn).Should().BeEquivalentTo(EndpointArn("healthy"), EndpointArn("other"));
        _sns.Verify(x => x.DeleteEndpointAsync(It.IsAny<DeleteEndpointRequest>(), It.IsAny<CancellationToken>()), Times.Never,
            "a transport failure is transient, so the registration is kept");
    }

    [Fact]
    public async Task SendToUsersAsync_TheCallersOwnCancellation_StillPropagates()
    {
        SetupUser("1", Row("a", "1", "a"));
        using var cts = new CancellationTokenSource();
        _sns.Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
            .Callback(cts.Cancel)
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var act = () => CreateSut().SendToUsersAsync([1], "Hello", "World", cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SendToUsersAsync_FollowsTheIndexPages()
    {
        var lastKey = new Dictionary<string, AttributeValue> { ["InstallationId"] = new() { S = "a" } };
        _dynamoDb.SetupSequence(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse { Items = [Row("a", "1", "a")], LastEvaluatedKey = lastKey })
            .ReturnsAsync(new QueryResponse { Items = [Row("b", "1", "b")] });

        await CreateSut().SendToUsersAsync([1], "Hello", "World", cancellationToken: TestContext.Current.CancellationToken);

        _published.Should().HaveCount(2);
        _dynamoDb.Verify(x => x.QueryAsync(
            It.Is<QueryRequest>(r => r.ExclusiveStartKey != null && r.ExclusiveStartKey["InstallationId"].S == "a"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BroadcastAsync_PublishesOnceToTheTopic()
    {
        var metadata = new Dictionary<string, string> { ["route"] = "/news" };

        await CreateSut().BroadcastAsync("Hello", "World", metadata, TestContext.Current.CancellationToken);

        _published.Should().ContainSingle();
        _published[0].TopicArn.Should().Be(TopicArn);
        _published[0].TargetArn.Should().BeNull();
        _published[0].MessageStructure.Should().Be("json");
        _published[0].Message.Should().Be(SnsPushMessage.Build("Hello", "World", metadata));
        _dynamoDb.Verify(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.PushNotifications;
using AWSCredentials = Amazon.Runtime.AWSCredentials;

namespace MMCA.Common.Aws.LocalStack.Tests;

/// <summary>
/// The shipped SNS mobile push pair, registered through <c>AddAmazonSnsPush</c> exactly as a host
/// would, against LocalStack's SNS platform endpoints and DynamoDB: the ownership rules the unit tier
/// can only assert as request shapes (the conditional write that refuses another device's id, the
/// handover on a matching push channel, the owner-scoped delete) run against the real condition
/// evaluator and the real index, and a user-targeted send and a broadcast are publishes SNS accepts and
/// delivers to the endpoint. LocalStack records every platform-endpoint delivery instead of calling FCM,
/// which is what lets the delivery be read back.
/// </summary>
public sealed class SnsPushLocalStackTests(LocalStackFixture localStack) : IAsyncLifetime
{
    private const string UserIdIndexName = "UserId-index";
    private const UserIdentifierType Owner = 41;
    private const UserIdentifierType Stranger = 97;

    private readonly string _name = $"mmca-{Guid.NewGuid():N}";
    private AmazonSimpleNotificationServiceClient _sns = null!;
    private AmazonDynamoDBClient _dynamoDb = null!;
    private string _topicArn = null!;
    private string _fcmApplicationArn = null!;
    private readonly ConcurrentQueue<Published> _published = new();
    private ServiceProvider _host = null!;

    private IPushDeviceRegistrar Registrar => _host.GetRequiredService<IPushDeviceRegistrar>();

    private INativePushSender Sender => _host.GetRequiredService<INativePushSender>();

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _sns = localStack.CreateSnsClient();
        _dynamoDb = localStack.CreateDynamoDbClient();

        await _dynamoDb.CreateTableAsync(
            new CreateTableRequest
            {
                TableName = _name,
                BillingMode = BillingMode.PAY_PER_REQUEST,
                AttributeDefinitions =
                [
                    new AttributeDefinition { AttributeName = "InstallationId", AttributeType = ScalarAttributeType.S },
                    new AttributeDefinition { AttributeName = "UserId", AttributeType = ScalarAttributeType.S },
                ],
                KeySchema = [new KeySchemaElement { AttributeName = "InstallationId", KeyType = KeyType.HASH }],
                GlobalSecondaryIndexes =
                [
                    new GlobalSecondaryIndex
                    {
                        IndexName = UserIdIndexName,
                        KeySchema = [new KeySchemaElement { AttributeName = "UserId", KeyType = KeyType.HASH }],
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    },
                ],
            },
            ct);

        _topicArn = (await _sns.CreateTopicAsync(new CreateTopicRequest { Name = _name }, ct)).TopicArn;
        _fcmApplicationArn = (await _sns.CreatePlatformApplicationAsync(
            new CreatePlatformApplicationRequest
            {
                Name = _name,
                Platform = "GCM",
                Attributes = new Dictionary<string, string> { ["PlatformCredential"] = "dummy-fcm-service-account" },
            },
            ct)).PlatformApplicationArn;

        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IAmazonSimpleNotificationService>(new RecordingSnsClient(LocalStackFixture.Credentials, localStack.SnsConfig(), _published));
        services.AddAmazonSnsPush(LocalStackFixture.ConfigurationWith(localStack.NativePushSettings(_name, _topicArn, _fcmApplicationArn)));
        _host = services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        await _dynamoDb.DeleteTableAsync(new DeleteTableRequest { TableName = _name });
        await _sns.DeletePlatformApplicationAsync(new DeletePlatformApplicationRequest { PlatformApplicationArn = _fcmApplicationArn });
        await _sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = _topicArn });
        _sns.Dispose();
        _dynamoDb.Dispose();
    }

    [Fact]
    public async Task Upsert_NewInstallation_CreatesAnEnabledEndpoint_SubscribedToTheTopic_AndAnOwnedRow()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await Registrar.UpsertAsync(Owner, Request("device-1", "token-a"), ct);

        result.IsSuccess.Should().BeTrue();
        var row = await RowAsync("device-1", ct);
        row.Should().NotBeNull();
        row!["UserId"].S.Should().Be("41");
        row["Platform"].S.Should().Be("fcmv1");
        row["PushChannel"].S.Should().Be("token-a");

        var endpoint = await _sns.GetEndpointAttributesAsync(new GetEndpointAttributesRequest { EndpointArn = row["EndpointArn"].S }, ct);
        endpoint.Attributes["Token"].Should().Be("token-a");
        endpoint.Attributes["Enabled"].Should().Be("true");
        (await TopicSubscriptionEndpointsAsync(ct)).Should().ContainSingle().Which.Should().Be(row["EndpointArn"].S);
        row["SubscriptionArn"].S.Should().StartWith(_topicArn);
    }

    [Fact]
    public async Task Upsert_AnotherDevicesInstallationId_IsAConflict_AndLeavesTheRowAndEndpointUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        (await Registrar.UpsertAsync(Owner, Request("device-1", "token-a"), ct)).IsSuccess.Should().BeTrue();
        var before = await RowAsync("device-1", ct);

        var result = await Registrar.UpsertAsync(Stranger, Request("device-1", "token-b"), ct);

        result.Errors.Should().ContainSingle().Which.Code.Should().Be("PushDevice.InstallationConflict");
        result.Errors[0].Type.Should().Be(ErrorType.Conflict);
        var after = await RowAsync("device-1", ct);
        after!["UserId"].S.Should().Be("41");
        after["PushChannel"].S.Should().Be("token-a");
        after["EndpointArn"].S.Should().Be(before!["EndpointArn"].S);
        (await _sns.GetEndpointAttributesAsync(new GetEndpointAttributesRequest { EndpointArn = after["EndpointArn"].S }, ct))
            .Attributes["Token"].Should().Be("token-a");
        (await PlatformEndpointsAsync(ct)).Should().ContainSingle("a refused registration leaves no endpoint behind");
        (await TopicSubscriptionEndpointsAsync(ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task Upsert_TheSameChannelFromAnotherUser_HandsTheInstallationOver()
    {
        var ct = TestContext.Current.CancellationToken;
        (await Registrar.UpsertAsync(Owner, Request("device-1", "shared-token"), ct)).IsSuccess.Should().BeTrue();
        var before = await RowAsync("device-1", ct);

        var result = await Registrar.UpsertAsync(Stranger, Request("device-1", "shared-token"), ct);

        result.IsSuccess.Should().BeTrue();
        var after = await RowAsync("device-1", ct);
        after!["UserId"].S.Should().Be("97");
        after["EndpointArn"].S.Should().Be(before!["EndpointArn"].S, "the device keeps its endpoint across the handover");
        (await QueryUserAsync("41", ct)).Should().BeEmpty("the previous owner's sends no longer reach the device");
        (await QueryUserAsync("97", ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task Delete_IsOwnerScoped_AndOnlyTheOwnerRemovesTheEndpointAndRow()
    {
        var ct = TestContext.Current.CancellationToken;
        (await Registrar.UpsertAsync(Owner, Request("device-1", "token-a"), ct)).IsSuccess.Should().BeTrue();
        var endpointArn = (await RowAsync("device-1", ct))!["EndpointArn"].S;

        (await Registrar.DeleteAsync(Stranger, "device-1", ct)).IsSuccess.Should().BeTrue();
        (await Registrar.DeleteAsync(Owner, "no-such-device", ct)).IsSuccess.Should().BeTrue();
        (await RowAsync("device-1", ct)).Should().NotBeNull("another user's delete removes nothing");
        (await PlatformEndpointsAsync(ct)).Should().ContainSingle();

        (await Registrar.DeleteAsync(Owner, "device-1", ct)).IsSuccess.Should().BeTrue();

        (await RowAsync("device-1", ct)).Should().BeNull();
        (await PlatformEndpointsAsync(ct)).Should().NotContain(endpointArn);
        (await TopicSubscriptionEndpointsAsync(ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task SendToUsers_PublishesToEachEndpointOfTheUser_InThePerPlatformStructure()
    {
        var ct = TestContext.Current.CancellationToken;
        (await Registrar.UpsertAsync(Owner, Request("phone", "token-phone"), ct)).IsSuccess.Should().BeTrue();
        (await Registrar.UpsertAsync(Owner, Request("tablet", "token-tablet"), ct)).IsSuccess.Should().BeTrue();
        (await Registrar.UpsertAsync(Stranger, Request("other", "token-other"), ct)).IsSuccess.Should().BeTrue();
        var phone = (await RowAsync("phone", ct))!["EndpointArn"].S;
        var tablet = (await RowAsync("tablet", ct))!["EndpointArn"].S;
        var other = (await RowAsync("other", ct))!["EndpointArn"].S;

        await Sender.SendToUsersAsync([Owner], "Hello", "World", new Dictionary<string, string> { ["route"] = "/inbox" }, ct);

        _published.Select(p => p.TargetArn).Should().BeEquivalentTo([phone, tablet], "one publish per endpoint of the targeted user");
        _published.Should().AllSatisfy(p => p.MessageId.Should().NotBeNullOrEmpty());

        var deliveries = await DeliveriesAsync(ct);
        foreach (var endpointArn in new[] { phone, tablet })
        {
            var delivery = deliveries.Should().ContainKey(endpointArn).WhoseValue.Should().ContainSingle().Subject;
            using var fcm = FcmEntryOf(delivery);
            fcm.RootElement.GetProperty("fcmV1Message").GetProperty("message").GetProperty("data")
                .GetProperty("route").GetString().Should().Be("/inbox");
        }

        deliveries.Should().NotContainKey(other, "a user-targeted send reaches only that user's devices");
    }

    [Fact]
    public async Task Broadcast_PublishesToTheTopic_AndReachesEverySubscribedEndpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        (await Registrar.UpsertAsync(Owner, Request("phone", "token-phone"), ct)).IsSuccess.Should().BeTrue();
        (await Registrar.UpsertAsync(Stranger, Request("other", "token-other"), ct)).IsSuccess.Should().BeTrue();
        var phone = (await RowAsync("phone", ct))!["EndpointArn"].S;
        var other = (await RowAsync("other", ct))!["EndpointArn"].S;

        await Sender.BroadcastAsync("Hello", "Everyone", cancellationToken: ct);

        var publish = _published.Should().ContainSingle().Subject;
        publish.TopicArn.Should().Be(_topicArn);
        publish.MessageId.Should().NotBeNullOrEmpty();

        var deliveries = await DeliveriesAsync(ct);
        foreach (var endpointArn in new[] { phone, other })
        {
            var delivery = deliveries.Should().ContainKey(endpointArn).WhoseValue.Should().ContainSingle().Subject;
            using var fcm = FcmEntryOf(delivery);
            fcm.RootElement.GetProperty("fcmV1Message").GetProperty("message").GetProperty("notification")
                .GetProperty("body").GetString().Should().Be("Everyone");
        }
    }

    /// <summary>
    /// The platform entry SNS selected for a recorded delivery. LocalStack records the delivered
    /// payload as a JSON string holding the message structure's <c>GCM</c> entry, so reading it back
    /// also proves SNS picked the FCM entry out of the structure.
    /// </summary>
    private static JsonDocument FcmEntryOf(JsonElement delivery)
    {
        using var recorded = JsonDocument.Parse(delivery.GetProperty("Message").GetString()!);
        return JsonDocument.Parse(recorded.RootElement.GetString()!);
    }

    private static DeviceInstallationRequest Request(string installationId, string pushChannel) => new()
    {
        InstallationId = installationId,
        Platform = DeviceInstallationRequest.FcmV1Platform,
        PushChannel = pushChannel,
    };

    private async Task<Dictionary<string, AttributeValue>?> RowAsync(string installationId, CancellationToken ct)
    {
        var response = await _dynamoDb.GetItemAsync(
            new GetItemRequest
            {
                TableName = _name,
                Key = new Dictionary<string, AttributeValue> { ["InstallationId"] = new() { S = installationId } },
                ConsistentRead = true,
            },
            ct);
        return response.Item is { Count: > 0 } item ? item : null;
    }

    private async Task<List<Dictionary<string, AttributeValue>>> QueryUserAsync(string userId, CancellationToken ct)
    {
        var response = await _dynamoDb.QueryAsync(
            new QueryRequest
            {
                TableName = _name,
                IndexName = UserIdIndexName,
                KeyConditionExpression = "UserId = :user",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":user"] = new() { S = userId } },
            },
            ct);
        return response.Items ?? [];
    }

    private async Task<List<string>> PlatformEndpointsAsync(CancellationToken ct)
    {
        var response = await _sns.ListEndpointsByPlatformApplicationAsync(
            new ListEndpointsByPlatformApplicationRequest { PlatformApplicationArn = _fcmApplicationArn }, ct);
        return [.. (response.Endpoints ?? []).Select(e => e.EndpointArn)];
    }

    private async Task<List<string>> TopicSubscriptionEndpointsAsync(CancellationToken ct)
    {
        var response = await _sns.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = _topicArn }, ct);
        return [.. (response.Subscriptions ?? []).Select(s => s.Endpoint)];
    }

    /// <summary>
    /// The messages LocalStack recorded for this test's platform endpoints, keyed by endpoint ARN (its
    /// <c>/_aws/sns/platform-endpoint-messages</c> retrospection endpoint).
    /// </summary>
    private async Task<Dictionary<string, List<JsonElement>>> DeliveriesAsync(CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = localStack.ServiceUrl };
        var body = await http.GetFromJsonAsync<JsonElement>(
            $"_aws/sns/platform-endpoint-messages?region={LocalStackFixture.Region}", ct);

        Dictionary<string, List<JsonElement>> deliveries = new(StringComparer.Ordinal);
        if (body.TryGetProperty("platform_endpoint_messages", out var byEndpoint))
        {
            foreach (var endpoint in byEndpoint.EnumerateObject())
            {
                deliveries[endpoint.Name] = [.. endpoint.Value.EnumerateArray().Select(m => m.Clone())];
            }
        }

        return deliveries;
    }

    /// <summary>A publish the adapter made and the MessageId SNS returned for it.</summary>
    private sealed record Published(string? TargetArn, string? TopicArn, string? MessageId);

    /// <summary>
    /// The LocalStack SNS client, registered BEFORE <c>AddAmazonSnsPush</c> (which then uses it as is,
    /// the host-registered-client posture) and recording each publish's response, since the sender
    /// itself returns nothing a test could read the MessageId from.
    /// </summary>
    private sealed class RecordingSnsClient(AWSCredentials credentials, AmazonSimpleNotificationServiceConfig config, ConcurrentQueue<Published> published)
        : AmazonSimpleNotificationServiceClient(credentials, config)
    {
        public override async Task<PublishResponse> PublishAsync(PublishRequest request, CancellationToken cancellationToken = default)
        {
            var response = await base.PublishAsync(request, cancellationToken);
            published.Enqueue(new Published(request.TargetArn, request.TopicArn, response.MessageId));
            return response;
        }
    }
}

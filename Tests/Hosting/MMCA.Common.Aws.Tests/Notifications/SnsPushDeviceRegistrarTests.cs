using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Aws.Notifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.PushNotifications;
using Moq;

namespace MMCA.Common.Aws.Tests.Notifications;

/// <summary>
/// The SNS device registrar (ADR-044) against mocked SNS and DynamoDB clients. The registry row is the
/// ownership store, so the tests pin the same contract the Notification Hubs registrar honours: a new
/// installation gets an endpoint, a topic subscription and a row; another user's installation is a
/// conflict unless the caller presents its push channel (the device changing hands); a refused write
/// leaves nothing behind; and a delete is owner-scoped with no existence oracle.
/// </summary>
public sealed class SnsPushDeviceRegistrarTests
{
    private const string Table = "installations";
    private const string TopicArn = "arn:aws:sns:us-east-1:000000000000:broadcast";
    private const string FcmAppArn = "arn:aws:sns:us-east-1:000000000000:app/GCM/fcm";
    private const string InstallationId = "3f0c2f9c1e8b4b6c9d3a5e7f0a1b2c3d";
    private const string ExistingEndpointArn = "arn:aws:sns:us-east-1:000000000000:endpoint/GCM/fcm/existing";
    private const string ExistingSubscriptionArn = "arn:aws:sns:us-east-1:000000000000:broadcast:existing-sub";
    private const string NewEndpointArn = "arn:aws:sns:us-east-1:000000000000:endpoint/GCM/fcm/new";
    private const string NewSubscriptionArn = "arn:aws:sns:us-east-1:000000000000:broadcast:new-sub";
    private const UserIdentifierType Owner = 42;
    private const UserIdentifierType Stranger = 99;

    private readonly Mock<IAmazonDynamoDB> _dynamoDb = new();
    private readonly Mock<IAmazonSimpleNotificationService> _sns = new();

    public SnsPushDeviceRegistrarTests()
    {
        _sns.Setup(x => x.CreatePlatformEndpointAsync(It.IsAny<CreatePlatformEndpointRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatePlatformEndpointResponse { EndpointArn = NewEndpointArn });
        _sns.Setup(x => x.SubscribeAsync(It.IsAny<SubscribeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubscribeResponse { SubscriptionArn = NewSubscriptionArn });
        _sns.Setup(x => x.SetEndpointAttributesAsync(It.IsAny<SetEndpointAttributesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SetEndpointAttributesResponse());
        _dynamoDb.Setup(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PutItemResponse());
        _dynamoDb.Setup(x => x.DeleteItemAsync(It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteItemResponse());

        // The owner holds no other row by default, so a delete is the endpoint's last row.
        _dynamoDb.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse());
        SetupNoRow();
    }

    private static SnsNativePushSettings Settings(string? apnsArn = null) => new()
    {
        InstallationsTable = Table,
        BroadcastTopicArn = TopicArn,
        FcmPlatformApplicationArn = FcmAppArn,
        ApnsPlatformApplicationArn = apnsArn,
    };

    private SnsPushDeviceRegistrar CreateSut(SnsNativePushSettings? settings = null)
    {
        var options = Options.Create(settings ?? Settings());
        return new SnsPushDeviceRegistrar(
            new SnsInstallationRegistry(_dynamoDb.Object, _sns.Object, options),
            _sns.Object,
            options,
            NullLogger<SnsPushDeviceRegistrar>.Instance);
    }

    private static DeviceInstallationRequest Request(string pushChannel = "token-new", string platform = "fcmv1") => new()
    {
        InstallationId = InstallationId,
        Platform = platform,
        PushChannel = pushChannel,
    };

    private void SetupNoRow() =>
        _dynamoDb.Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetItemResponse());

    private void SetupRow(UserIdentifierType owner, string pushChannel) =>
        _dynamoDb.Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetItemResponse
            {
                Item = new Dictionary<string, AttributeValue>
                {
                    ["InstallationId"] = new() { S = InstallationId },
                    ["UserId"] = new() { S = owner.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    ["Platform"] = new() { S = "fcmv1" },
                    ["PushChannel"] = new() { S = pushChannel },
                    ["EndpointArn"] = new() { S = ExistingEndpointArn },
                    ["SubscriptionArn"] = new() { S = ExistingSubscriptionArn },
                },
            });

    private void VerifyNoSnsMutation()
    {
        _sns.Verify(x => x.CreatePlatformEndpointAsync(It.IsAny<CreatePlatformEndpointRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _sns.Verify(x => x.SetEndpointAttributesAsync(It.IsAny<SetEndpointAttributesRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _sns.Verify(x => x.SubscribeAsync(It.IsAny<SubscribeRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _sns.Verify(x => x.UnsubscribeAsync(It.IsAny<UnsubscribeRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _sns.Verify(x => x.DeleteEndpointAsync(It.IsAny<DeleteEndpointRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void OwnershipCondition_ExpressesTheHandoverRule() =>
        SnsInstallationRegistry.OwnershipCondition.Should().Be(
            "attribute_not_exists(InstallationId) OR UserId = :me OR PushChannel = :channel");

    [Fact]
    public async Task UpsertAsync_NewInstallation_CreatesEndpointSubscriptionAndRow()
    {
        var result = await CreateSut().UpsertAsync(Owner, Request(), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _sns.Verify(x => x.CreatePlatformEndpointAsync(
            It.Is<CreatePlatformEndpointRequest>(r => r.PlatformApplicationArn == FcmAppArn && r.Token == "token-new"),
            It.IsAny<CancellationToken>()), Times.Once);
        _sns.Verify(x => x.SubscribeAsync(
            It.Is<SubscribeRequest>(r => r.TopicArn == TopicArn && r.Protocol == "application" && r.Endpoint == NewEndpointArn),
            It.IsAny<CancellationToken>()), Times.Once);
        _dynamoDb.Verify(x => x.PutItemAsync(
            It.Is<PutItemRequest>(r =>
                r.TableName == Table
                && r.ConditionExpression == SnsInstallationRegistry.OwnershipCondition
                && r.ExpressionAttributeValues[":me"].S == "42"
                && r.ExpressionAttributeValues[":channel"].S == "token-new"
                && r.Item["InstallationId"].S == InstallationId
                && r.Item["UserId"].S == "42"
                && r.Item["Platform"].S == "fcmv1"
                && r.Item["PushChannel"].S == "token-new"
                && r.Item["EndpointArn"].S == NewEndpointArn
                && r.Item["SubscriptionArn"].S == NewSubscriptionArn),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpsertAsync_SameUserReRegisters_UpdatesTheTokenOnTheExistingEndpoint()
    {
        SetupRow(Owner, "token-old");

        var result = await CreateSut().UpsertAsync(Owner, Request("token-rotated"), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _sns.Verify(x => x.SetEndpointAttributesAsync(
            It.Is<SetEndpointAttributesRequest>(r =>
                r.EndpointArn == ExistingEndpointArn
                && r.Attributes["Token"] == "token-rotated"
                && r.Attributes["Enabled"] == "true"),
            It.IsAny<CancellationToken>()), Times.Once);
        _sns.Verify(x => x.CreatePlatformEndpointAsync(It.IsAny<CreatePlatformEndpointRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _sns.Verify(x => x.SubscribeAsync(It.IsAny<SubscribeRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _dynamoDb.Verify(x => x.PutItemAsync(
            It.Is<PutItemRequest>(r =>
                r.Item["PushChannel"].S == "token-rotated"
                && r.Item["EndpointArn"].S == ExistingEndpointArn
                && r.Item["SubscriptionArn"].S == ExistingSubscriptionArn),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpsertAsync_ExistingEndpointGone_CreatesAFreshOne()
    {
        SetupRow(Owner, "token-old");
        _sns.Setup(x => x.SetEndpointAttributesAsync(It.IsAny<SetEndpointAttributesRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Amazon.SimpleNotificationService.Model.NotFoundException("Endpoint does not exist"));

        var result = await CreateSut().UpsertAsync(Owner, Request("token-rotated"), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _sns.Verify(x => x.CreatePlatformEndpointAsync(It.IsAny<CreatePlatformEndpointRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        _dynamoDb.Verify(x => x.PutItemAsync(
            It.Is<PutItemRequest>(r => r.Item["EndpointArn"].S == NewEndpointArn && r.Item["SubscriptionArn"].S == NewSubscriptionArn),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpsertAsync_AnotherUsersInstallationWithADifferentChannel_IsAConflictAndTouchesNothing()
    {
        SetupRow(Stranger, "their-token");

        var result = await CreateSut().UpsertAsync(Owner, Request("my-token"), TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("PushDevice.InstallationConflict");
        result.Errors[0].Type.Should().Be(ErrorType.Conflict);
        VerifyNoSnsMutation();
        _dynamoDb.Verify(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _dynamoDb.Verify(x => x.DeleteItemAsync(It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpsertAsync_WriteRefusedByTheCondition_IsAConflictAndCleansUpWhatItCreated()
    {
        // The row appeared between the read and the conditional write (another device claimed the id).
        _dynamoDb.Setup(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConditionalCheckFailedException("The conditional request failed"));

        var result = await CreateSut().UpsertAsync(Owner, Request("my-token"), TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("PushDevice.InstallationConflict");
        _sns.Verify(x => x.UnsubscribeAsync(
            It.Is<UnsubscribeRequest>(r => r.SubscriptionArn == NewSubscriptionArn), It.IsAny<CancellationToken>()), Times.Once);
        _sns.Verify(x => x.DeleteEndpointAsync(
            It.Is<DeleteEndpointRequest>(r => r.EndpointArn == NewEndpointArn), It.IsAny<CancellationToken>()), Times.Once);
        _dynamoDb.Verify(x => x.DeleteItemAsync(It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpsertAsync_AnotherUsersInstallationWithTheSameChannel_IsHandedOver()
    {
        SetupRow(Stranger, "shared-device-token");

        var result = await CreateSut().UpsertAsync(Owner, Request("shared-device-token"), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _dynamoDb.Verify(x => x.PutItemAsync(
            It.Is<PutItemRequest>(r =>
                r.Item["UserId"].S == "42"
                && r.Item["EndpointArn"].S == ExistingEndpointArn
                && r.ExpressionAttributeValues[":channel"].S == "shared-device-token"),
            It.IsAny<CancellationToken>()), Times.Once);
        _sns.Verify(x => x.CreatePlatformEndpointAsync(It.IsAny<CreatePlatformEndpointRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _sns.Verify(x => x.DeleteEndpointAsync(It.IsAny<DeleteEndpointRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("wns")]
    [InlineData("")]
    [InlineData("gcm")]
    public async Task UpsertAsync_UnsupportedPlatform_FailsValidation(string platform)
    {
        var result = await CreateSut().UpsertAsync(Owner, Request(platform: platform), TestContext.Current.CancellationToken);

        result.Errors.Should().ContainSingle().Which.Type.Should().Be(ErrorType.Validation);
        result.Errors[0].Code.Should().Be("PushDevice.UnsupportedPlatform");
        VerifyNoSnsMutation();
        _dynamoDb.Verify(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpsertAsync_PlatformWithoutAConfiguredApplication_FailsValidation()
    {
        var result = await CreateSut(Settings(apnsArn: null)).UpsertAsync(Owner, Request(platform: "apns"), TestContext.Current.CancellationToken);

        result.Errors.Should().ContainSingle().Which.Type.Should().Be(ErrorType.Validation);
        result.Errors[0].Message.Should().Contain("apns");
        VerifyNoSnsMutation();
    }

    [Fact]
    public async Task UpsertAsync_ApnsWithAConfiguredApplication_UsesThatApplication()
    {
        const string apnsArn = "arn:aws:sns:us-east-1:000000000000:app/APNS/ios";

        var result = await CreateSut(Settings(apnsArn)).UpsertAsync(Owner, Request(platform: "APNS"), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _sns.Verify(x => x.CreatePlatformEndpointAsync(
            It.Is<CreatePlatformEndpointRequest>(r => r.PlatformApplicationArn == apnsArn), It.IsAny<CancellationToken>()), Times.Once);
        _dynamoDb.Verify(x => x.PutItemAsync(It.Is<PutItemRequest>(r => r.Item["Platform"].S == "apns"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpsertAsync_ServiceFailure_ReturnsAFailureResult()
    {
        _dynamoDb.Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonDynamoDBException("boom"));

        var result = await CreateSut().UpsertAsync(Owner, Request(), TestContext.Current.CancellationToken);

        result.Errors.Should().ContainSingle().Which.Code.Should().Be("PushDevice.UpsertFailed");
    }

    // L252: a transport failure the AWS SDK lets escape is the same UpsertFailed Result as a service
    // failure, not an exception the endpoint turns into a 500.
    [Fact]
    public async Task UpsertAsync_TransportFailure_ReturnsAFailureResult()
    {
        _dynamoDb.Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection reset"));

        var result = await CreateSut().UpsertAsync(Owner, Request(), TestContext.Current.CancellationToken);

        result.Errors.Should().ContainSingle().Which.Code.Should().Be("PushDevice.UpsertFailed");
    }

    [Fact]
    public async Task DeleteAsync_ByTheOwner_RemovesSubscriptionEndpointAndRow()
    {
        SetupRow(Owner, "token");

        var result = await CreateSut().DeleteAsync(Owner, InstallationId, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _sns.Verify(x => x.UnsubscribeAsync(
            It.Is<UnsubscribeRequest>(r => r.SubscriptionArn == ExistingSubscriptionArn), It.IsAny<CancellationToken>()), Times.Once);
        _sns.Verify(x => x.DeleteEndpointAsync(
            It.Is<DeleteEndpointRequest>(r => r.EndpointArn == ExistingEndpointArn), It.IsAny<CancellationToken>()), Times.Once);
        _dynamoDb.Verify(x => x.DeleteItemAsync(
            It.Is<DeleteItemRequest>(r =>
                r.TableName == Table
                && r.Key["InstallationId"].S == InstallationId
                && r.ConditionExpression == "UserId = :me"
                && r.ExpressionAttributeValues[":me"].S == "42"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // M260: a second installation id of the same user (an app reinstall) still holds the endpoint,
    // so deleting the first row must leave the endpoint and its subscription alone.
    [Fact]
    public async Task DeleteAsync_WhenAnotherRowOfTheOwnerSharesTheEndpoint_DeletesOnlyTheRow()
    {
        const string otherInstallationId = "9a8b7c6d5e4f40312a1b2c3d4e5f6a7b";
        SetupRow(Owner, "token");
        _dynamoDb.Setup(x => x.QueryAsync(
                It.Is<QueryRequest>(r => r.IndexName == SnsInstallationRegistry.UserIdIndexName && r.ExpressionAttributeValues[":user"].S == "42"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse { Items = [OwnerRow(InstallationId), OwnerRow(otherInstallationId)] });

        var result = await CreateSut().DeleteAsync(Owner, InstallationId, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _sns.Verify(x => x.DeleteEndpointAsync(It.IsAny<DeleteEndpointRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _sns.Verify(x => x.UnsubscribeAsync(It.IsAny<UnsubscribeRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _dynamoDb.Verify(x => x.DeleteItemAsync(It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        _dynamoDb.Verify(x => x.DeleteItemAsync(
            It.Is<DeleteItemRequest>(r => r.Key["InstallationId"].S == InstallationId), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Dictionary<string, AttributeValue> OwnerRow(string installationId) => new()
    {
        ["InstallationId"] = new() { S = installationId },
        ["UserId"] = new() { S = "42" },
        ["Platform"] = new() { S = "fcmv1" },
        ["PushChannel"] = new() { S = "token" },
        ["EndpointArn"] = new() { S = ExistingEndpointArn },
        ["SubscriptionArn"] = new() { S = ExistingSubscriptionArn },
    };

    [Fact]
    public async Task DeleteAsync_ByAnotherUser_SucceedsAndDeletesNothing()
    {
        SetupRow(Stranger, "token");

        var result = await CreateSut().DeleteAsync(Owner, InstallationId, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        VerifyNoSnsMutation();
        _dynamoDb.Verify(x => x.DeleteItemAsync(It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_UnknownInstallation_SucceedsAndDeletesNothing()
    {
        var result = await CreateSut().DeleteAsync(Owner, InstallationId, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        VerifyNoSnsMutation();
        _dynamoDb.Verify(x => x.DeleteItemAsync(It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_ServiceFailure_ReturnsAFailureResult()
    {
        SetupRow(Owner, "token");
        _sns.Setup(x => x.DeleteEndpointAsync(It.IsAny<DeleteEndpointRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonSimpleNotificationServiceException("boom"));

        var result = await CreateSut().DeleteAsync(Owner, InstallationId, TestContext.Current.CancellationToken);

        result.Errors.Should().ContainSingle().Which.Code.Should().Be("PushDevice.DeleteFailed");
        _dynamoDb.Verify(x => x.DeleteItemAsync(It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_TransportFailure_ReturnsAFailureResult()
    {
        SetupRow(Owner, "token");
        _sns.Setup(x => x.DeleteEndpointAsync(It.IsAny<DeleteEndpointRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("The response ended prematurely"));

        var result = await CreateSut().DeleteAsync(Owner, InstallationId, TestContext.Current.CancellationToken);

        result.Errors.Should().ContainSingle().Which.Code.Should().Be("PushDevice.DeleteFailed");
        _dynamoDb.Verify(x => x.DeleteItemAsync(It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using FirebaseAdmin.Messaging;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Gcp.Notifications;
using MMCA.Common.Infrastructure.Notifications.Push;

namespace MMCA.Common.Gcp.Tests.Notifications;

/// <summary>
/// The FCM native push sender (ADR-044) against a scripted registry and a recording FCM client:
/// user-targeted sends resolve the users' tokens and send one message per token, at most 500 per
/// <c>SendEachAsync</c> call; a dead token is pruned while the others still receive; the message
/// carries the same notification, data and APNs fields as the core payload builders; a broadcast is
/// one message to the topic.
/// </summary>
public sealed class FcmNativePushSenderTests
{
    private const string Topic = "broadcast";

    private readonly FakeFcmInstallationRegistry _registry = new();
    private readonly FakeFcmMessagingClient _messaging = new();

    private FcmNativePushSender CreateSut() =>
        new(
            _registry,
            _messaging,
            Options.Create(new FcmNativePushSettings
            {
                ProjectId = "demo-project",
                InstallationsCollection = "pushInstallations",
                BroadcastTopic = Topic,
            }),
            NullLogger<FcmNativePushSender>.Instance);

    private void AddInstallation(string id, string userId, string? token = null) =>
        _registry.Installations[id] = new FcmInstallation(id, userId, "fcmv1", token ?? $"token-{id}");

    private List<string> SentTokens() =>
        [.. _messaging.SendEachCalls.SelectMany(call => call).Select(FakeFcmMessagingClient.TokenOf)];

    [Fact]
    public async Task SendToUsersAsync_SendsOneMessagePerTokenAcrossUsers()
    {
        AddInstallation("a", "1");
        AddInstallation("b", "1");
        AddInstallation("c", "2");
        AddInstallation("d", "2", token: "token-a");
        AddInstallation("e", "3");

        await CreateSut().SendToUsersAsync([1, 2, 2], "Hello", "World", cancellationToken: TestContext.Current.CancellationToken);

        SentTokens().Should().BeEquivalentTo(["token-a", "token-b", "token-c"], "user 3 was not targeted and installation d shares installation a's token");
        _registry.Queries.Should().ContainSingle().Which.Should().BeEquivalentTo("1", "2");
    }

    [Fact]
    public async Task SendToUsersAsync_ChunksAt500MessagesPerCall()
    {
        for (var i = 0; i < 1201; i++)
        {
            AddInstallation(i.ToString(CultureInfo.InvariantCulture), "1");
        }

        await CreateSut().SendToUsersAsync([1], "Hello", "World", cancellationToken: TestContext.Current.CancellationToken);

        _messaging.SendEachCalls.Select(call => call.Count).Should().Equal(500, 500, 201);
        SentTokens().Should().OnlyHaveUniqueItems().And.HaveCount(1201);
    }

    [Fact]
    public async Task SendToUsersAsync_QueriesAtMost30UsersAtATime()
    {
        await CreateSut().SendToUsersAsync(Enumerable.Range(1, 61), "Hello", "World", cancellationToken: TestContext.Current.CancellationToken);

        _registry.Queries.Select(q => q.Count).Should().Equal(30, 30, 1);
    }

    [Fact]
    public async Task SendToUsersAsync_AnUnregisteredTokenIsPruned_AndTheOthersStillDelivered()
    {
        AddInstallation("dead", "1");
        AddInstallation("healthy", "1");
        AddInstallation("other", "2");
        _messaging.OutcomeFor = token => token == "token-dead"
            ? new FcmSendOutcome(false, MessagingErrorCode.Unregistered, new InvalidOperationException("Requested entity was not found."))
            : FcmSendOutcome.Success;

        await CreateSut().SendToUsersAsync([1, 2], "Hello", "World", cancellationToken: TestContext.Current.CancellationToken);

        SentTokens().Should().BeEquivalentTo("token-dead", "token-healthy", "token-other");
        _registry.PushChannelDeletes.Should().Equal(("dead", "token-dead"));
        _registry.OwnedDeletes.Should().BeEmpty();
    }

    [Fact]
    public async Task SendToUsersAsync_AnInvalidRegistrationToken_IsPruned()
    {
        AddInstallation("malformed", "1");
        _messaging.OutcomeFor = _ => new FcmSendOutcome(
            false, MessagingErrorCode.InvalidArgument, new InvalidOperationException("The registration token is not a valid FCM registration token"));

        await CreateSut().SendToUsersAsync([1], "Hello", "World", cancellationToken: TestContext.Current.CancellationToken);

        _registry.PushChannelDeletes.Should().Equal(("malformed", "token-malformed"));
    }

    [Theory]
    [InlineData(MessagingErrorCode.InvalidArgument, "Invalid JSON payload received. Unknown name \"badKey\"")]
    [InlineData(MessagingErrorCode.Unavailable, "The service is unavailable")]
    [InlineData(MessagingErrorCode.QuotaExceeded, "Quota exceeded")]
    public async Task SendToUsersAsync_AFailureThatIsNotAboutTheToken_PrunesNothing(MessagingErrorCode code, string message)
    {
        AddInstallation("a", "1");
        AddInstallation("b", "1");
        _messaging.OutcomeFor = _ => new FcmSendOutcome(false, code, new InvalidOperationException(message));

        await CreateSut().SendToUsersAsync([1], "Hello", "World", cancellationToken: TestContext.Current.CancellationToken);

        SentTokens().Should().HaveCount(2);
        _registry.PushChannelDeletes.Should().BeEmpty();
    }

    [Fact]
    public async Task SendToUsersAsync_ARegistryLookupFailure_SkipsThoseUsersWithoutThrowing()
    {
        _registry.QueryException = new RpcException(new Status(StatusCode.Unavailable, "boom"));

        var act = () => CreateSut().SendToUsersAsync([1], "Hello", "World", cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        _messaging.SendEachCalls.Should().BeEmpty();
    }

    [Fact]
    public void Message_CarriesTitleBodyMetadataAndTheApnsBlock_TheSameFieldsAsTheCorePayloads()
    {
        var metadata = new Dictionary<string, string> { ["route"] = "/notifications/7", ["aps"] = "must-not-clobber" };

        var wire = FakeFcmMessagingClient.Wire(FcmPushMessage.ForToken("token-1", "Hello", "World", metadata));

        var fcm = JsonNode.Parse(NativePushPayloads.BuildFcmV1Payload("Hello", "World", metadata))!["message"]!;
        var apns = JsonNode.Parse(NativePushPayloads.BuildApnsPayload("Hello", "World", metadata))!;
        var actual = JsonNode.Parse(wire.GetRawText())!;

        actual.AsObject().Select(p => p.Key).Should().BeEquivalentTo("token", "notification", "data", "apns");
        actual["token"]!.GetValue<string>().Should().Be("token-1");
        JsonNode.DeepEquals(actual["notification"], fcm["notification"]).Should().BeTrue(actual["notification"]!.ToJsonString());
        JsonNode.DeepEquals(actual["data"], fcm["data"]).Should().BeTrue(actual["data"]!.ToJsonString());
        JsonNode.DeepEquals(actual["apns"]!["payload"], apns).Should().BeTrue(actual["apns"]!.ToJsonString());

        // Spelled out as well, so a change to the core builders cannot silently move both sides.
        actual["notification"]!.ToJsonString().Should().Be("""{"title":"Hello","body":"World"}""");
        actual["data"]!["route"]!.GetValue<string>().Should().Be("/notifications/7");
        actual["apns"]!["payload"]!["aps"]!.ToJsonString().Should().Be("""{"alert":{"title":"Hello","body":"World"}}""");
        actual["apns"]!["payload"]!["route"]!.GetValue<string>().Should().Be("/notifications/7");
    }

    [Fact]
    public void Message_WithoutMetadata_HasNoDataAndNoCustomKeys()
    {
        var wire = FakeFcmMessagingClient.Wire(FcmPushMessage.ForToken("token-1", "Hello", "World", metadata: null));

        wire.TryGetProperty("data", out _).Should().BeFalse();
        wire.GetProperty("apns").GetProperty("payload").EnumerateObject().Select(p => p.Name).Should().Equal("aps");
    }

    [Fact]
    public async Task BroadcastAsync_SendsOneMessageToTheTopic()
    {
        var metadata = new Dictionary<string, string> { ["route"] = "/announcements" };

        await CreateSut().BroadcastAsync("Hello", "World", metadata, TestContext.Current.CancellationToken);

        var sent = _messaging.Sent.Should().ContainSingle().Subject;
        sent.Topic.Should().Be(Topic);
        var wire = FakeFcmMessagingClient.Wire(sent);
        wire.TryGetProperty("token", out _).Should().BeFalse();
        wire.GetProperty("notification").GetProperty("title").GetString().Should().Be("Hello");
        wire.GetProperty("data").GetProperty("route").GetString().Should().Be("/announcements");
        _messaging.SendEachCalls.Should().BeEmpty();
        _registry.Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task BroadcastAsync_AFailurePropagates()
    {
        _messaging.SendException = new InvalidOperationException("boom");

        var act = () => CreateSut().BroadcastAsync("Hello", "World", cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void WireJson_IsTheHttpV1MessageObject() =>
        JsonDocument.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(FcmPushMessage.ForTopic(Topic, "t", "b", null)))
            .RootElement.GetProperty("topic").GetString().Should().Be(Topic);
}

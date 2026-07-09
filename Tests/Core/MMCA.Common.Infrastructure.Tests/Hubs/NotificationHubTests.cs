using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using MMCA.Common.Infrastructure.Hubs;
using MMCA.Common.Infrastructure.Settings;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Hubs;

public sealed class NotificationHubTests
{
    private const string ConnectionId = "connection-1";

    private static (NotificationHub Hub, Mock<IGroupManager> Groups) CreateHub(string? channelKeyPattern = null)
    {
        PushNotificationSettings settings = channelKeyPattern is null
            ? new PushNotificationSettings()
            : new PushNotificationSettings { ChannelKeyPattern = channelKeyPattern };

        var groups = new Mock<IGroupManager>();
        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns(ConnectionId);

        var hub = new NotificationHub(Options.Create(settings))
        {
            Groups = groups.Object,
            Context = context.Object,
        };

        return (hub, groups);
    }

    // ── Method-name constants ──
    [Fact]
    public void ReceiveNotificationMethod_HasExpectedValue() =>
        NotificationHub.ReceiveNotificationMethod.Should().Be("ReceiveNotification");

    [Fact]
    public void ReceiveChannelEventMethod_HasExpectedValue() =>
        NotificationHub.ReceiveChannelEventMethod.Should().Be("ReceiveChannelEvent");

    [Fact]
    public void JoinChannelMethod_HasExpectedValue() =>
        NotificationHub.JoinChannelMethod.Should().Be("JoinChannel");

    [Fact]
    public void LeaveChannelMethod_HasExpectedValue() =>
        NotificationHub.LeaveChannelMethod.Should().Be("LeaveChannel");

    // ── Type shape ──
    [Fact]
    public void NotificationHub_HasAuthorizeAttribute()
    {
        var attribute = typeof(NotificationHub)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false);

        attribute.Should().ContainSingle();
    }

    [Fact]
    public void NotificationHub_InheritsFromHub() =>
        typeof(NotificationHub).Should().BeDerivedFrom<Microsoft.AspNetCore.SignalR.Hub>();

    [Fact]
    public void NotificationHub_IsSealed() =>
        typeof(NotificationHub).IsSealed.Should().BeTrue();

    // ── JoinChannelAsync ──
    [Theory]
    [InlineData("event:1")]
    [InlineData("session:123")]
    public async Task JoinChannelAsync_WithValidKey_AddsConnectionToGroup(string channelKey)
    {
        (NotificationHub hub, Mock<IGroupManager> groups) = CreateHub();

        await hub.JoinChannelAsync(channelKey);

        groups.Verify(
            g => g.AddToGroupAsync(ConnectionId, channelKey, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bogus")]
    [InlineData("event:")]
    [InlineData("event:abc")]
    [InlineData("EVENT:1")]
    [InlineData("event:1;extra")]
    public async Task JoinChannelAsync_WithInvalidKey_ThrowsHubException(string channelKey)
    {
        (NotificationHub hub, Mock<IGroupManager> groups) = CreateHub();

        var act = () => hub.JoinChannelAsync(channelKey);

        await act.Should().ThrowAsync<HubException>();
        groups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── LeaveChannelAsync ──
    [Fact]
    public async Task LeaveChannelAsync_WithValidKey_RemovesConnectionFromGroup()
    {
        (NotificationHub hub, Mock<IGroupManager> groups) = CreateHub();

        await hub.LeaveChannelAsync("event:1");

        groups.Verify(
            g => g.RemoveFromGroupAsync(ConnectionId, "event:1", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task LeaveChannelAsync_WithInvalidKey_ThrowsHubException()
    {
        (NotificationHub hub, Mock<IGroupManager> groups) = CreateHub();

        var act = () => hub.LeaveChannelAsync("not-a-channel");

        await act.Should().ThrowAsync<HubException>();
        groups.Verify(
            g => g.RemoveFromGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Configured pattern ──
    [Fact]
    public async Task JoinChannelAsync_WithCustomPattern_UsesConfiguredPattern()
    {
        (NotificationHub hub, Mock<IGroupManager> groups) = CreateHub("^room:[a-z]+$");

        await hub.JoinChannelAsync("room:main");
        var act = () => hub.JoinChannelAsync("event:1");

        await act.Should().ThrowAsync<HubException>();
        groups.Verify(
            g => g.AddToGroupAsync(ConnectionId, "room:main", It.IsAny<CancellationToken>()),
            Times.Once);
    }
}

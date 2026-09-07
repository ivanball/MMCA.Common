using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using MMCA.Common.Infrastructure.Notifications;
using MMCA.Common.Infrastructure.Notifications.Push;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Hubs;

/// <summary>
/// SEC-ADC-25: the hub is <c>[Authorize]</c> but had no connection cap, so one valid token could
/// open unbounded WebSockets and exhaust the replica, stopping live notifications for everyone.
/// </summary>
public sealed class NotificationHubConnectionCapTests
{
    private static NotificationHub CreateHub(string userId, int maxConnectionsPerUser)
    {
        var settings = new PushNotificationSettings { MaxConnectionsPerUser = maxConnectionsPerUser };

        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns(Guid.NewGuid().ToString());
        context.SetupGet(c => c.UserIdentifier).Returns(userId);
        context.SetupGet(c => c.User).Returns(new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], authenticationType: "TestAuth")));

        return new NotificationHub(Options.Create(settings))
        {
            Groups = Mock.Of<IGroupManager>(),
            Context = context.Object,
        };
    }

    [Fact]
    public async Task OnConnectedAsync_RefusesThePastCapConnection()
    {
        var userId = $"cap-{Guid.NewGuid()}";
        var hubs = new List<NotificationHub>();

        for (var i = 0; i < 3; i++)
        {
            var hub = CreateHub(userId, maxConnectionsPerUser: 3);
            hubs.Add(hub);
            await hub.OnConnectedAsync();
        }

        var overflow = CreateHub(userId, maxConnectionsPerUser: 3);
        Func<Task> connect = () => overflow.OnConnectedAsync();

        await connect.Should().ThrowAsync<HubException>();

        // Clean up, so the shared per-user counter does not leak into another test.
        foreach (var hub in hubs)
        {
            await hub.OnDisconnectedAsync(exception: null);
        }
    }

    [Fact]
    public async Task OnDisconnectedAsync_ReleasesTheSlot()
    {
        var userId = $"release-{Guid.NewGuid()}";
        var first = CreateHub(userId, maxConnectionsPerUser: 1);
        await first.OnConnectedAsync();

        var second = CreateHub(userId, maxConnectionsPerUser: 1);
        Func<Task> connect = () => second.OnConnectedAsync();
        await connect.Should().ThrowAsync<HubException>();

        await first.OnDisconnectedAsync(exception: null);

        // The refused attempt must not have consumed a slot of its own, or a user would stay locked
        // out after their live connections drained.
        var third = CreateHub(userId, maxConnectionsPerUser: 1);
        await third.OnConnectedAsync();
        await third.OnDisconnectedAsync(exception: null);
    }

    [Fact]
    public async Task OnConnectedAsync_WithTheCapDisabled_AcceptsEveryConnection()
    {
        var userId = $"uncapped-{Guid.NewGuid()}";
        var hubs = new List<NotificationHub>();

        for (var i = 0; i < 50; i++)
        {
            var hub = CreateHub(userId, maxConnectionsPerUser: 0);
            hubs.Add(hub);
            await hub.OnConnectedAsync();
        }

        hubs.Should().HaveCount(50);

        foreach (var hub in hubs)
        {
            await hub.OnDisconnectedAsync(exception: null);
        }
    }

    [Fact]
    public void DefaultSettings_CapConnectionsPerUser()
        => new PushNotificationSettings().MaxConnectionsPerUser.Should().BeGreaterThan(0);
}

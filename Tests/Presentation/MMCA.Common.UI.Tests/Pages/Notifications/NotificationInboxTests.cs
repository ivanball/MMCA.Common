using System.Globalization;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.UserNotifications;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Pages.Notifications;
using MMCA.Common.UI.Services.Notifications;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Notifications;

/// <summary>
/// bUnit tests for the <see cref="NotificationInbox"/> page — load/empty states and the mark-one /
/// mark-all interactions (service calls + shared unread-count update).
/// </summary>
public sealed class NotificationInboxTests : BunitTestBase
{
    private readonly Mock<INotificationInboxUIService> _inbox = new();
    private readonly NotificationState _state = new();

    public NotificationInboxTests()
    {
        Services.AddSingleton(_inbox.Object);
        Services.AddSingleton(_state);
    }

    private static PagedCollectionResult<UserNotificationDTO> Inbox(params UserNotificationDTO[] items)
        => new(items, new PaginationMetadata(items.Length, 20, 1));

    private static UserNotificationDTO Unread(int id)
        => new()
        {
            Id = id,
            PushNotificationId = id,
            Title = string.Create(CultureInfo.InvariantCulture, $"Notice {id}"),
            Body = "body",
            IsRead = false,
            SentOn = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc),
        };

    [Fact]
    public void WhenInboxEmpty_RendersEmptyState()
    {
        _inbox
            .Setup(x => x.GetInboxAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Inbox());

        var cut = RenderUnderTest<NotificationInbox>(_ => { });

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("You have no notifications."));
    }

    [Fact]
    public void WhenInboxHasItems_RendersTitlesAndMarkAllButton()
    {
        _inbox
            .Setup(x => x.GetInboxAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Inbox(Unread(1), Unread(2)));

        var cut = RenderUnderTest<NotificationInbox>(_ => { });

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Notice 1");
            cut.Markup.Should().Contain("Notice 2");
            cut.Markup.Should().Contain("Mark All as Read");
        });
    }

    [Fact]
    public void ClickingMarkAsRead_MarksThatNotificationRead()
    {
        _inbox
            .Setup(x => x.GetInboxAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Inbox(Unread(7)));
        _inbox
            .Setup(x => x.GetUnreadCountAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var cut = RenderUnderTest<NotificationInbox>(_ => { });
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Notice 7"));

        cut.Find("button[aria-label=\"Mark as read\"]").Click();

        cut.WaitForAssertion(() =>
            _inbox.Verify(x => x.MarkReadAsync(7, It.IsAny<CancellationToken>()), Times.Once()));
    }

    [Fact]
    public void ClickingMarkAllAsRead_MarksAllAndZeroesSharedCount()
    {
        _inbox
            .Setup(x => x.GetInboxAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Inbox(Unread(1), Unread(2)));
        _state.SetUnreadCount(2);

        var cut = RenderUnderTest<NotificationInbox>(_ => { });
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Mark All as Read"));

        cut.ClickButtonByText("Mark All as Read");

        cut.WaitForAssertion(() =>
            _inbox.Verify(x => x.MarkAllReadAsync(It.IsAny<CancellationToken>()), Times.Once()));
        _state.UnreadCount.Should().Be(0);
    }
}

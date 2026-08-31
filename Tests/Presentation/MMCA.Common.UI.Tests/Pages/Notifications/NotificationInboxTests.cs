using System.Globalization;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.UserNotifications;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Pages.Notifications;
using MMCA.Common.UI.Services.Notifications;
using Moq;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Pages.Notifications;

/// <summary>
/// bUnit tests for the <see cref="NotificationInbox"/> page: load/empty states, the mark-one /
/// mark-all interactions (service calls + shared unread-count update), and the failure surfaces:
/// every failed call raises exactly one toast and leaves what is already on screen alone rather
/// than blanking the list or zeroing the badge.
/// </summary>
public sealed class NotificationInboxTests : BunitTestBase
{
    private readonly Mock<INotificationInboxUIService> _inbox = new();
    private readonly NotificationState _state = new();
    private readonly Mock<IToastService> _toast = new();
    private readonly Mock<IScrollManager> _scroll = new();

    public NotificationInboxTests()
    {
        Services.AddSingleton(_inbox.Object);
        Services.AddSingleton(_state);
        // Registered after the base class's default facade, so this wins and the page's toast
        // surface can be counted without rendering a snackbar provider.
        Services.AddSingleton<IToastService>(_toast.Object);
        // Same reasoning for MudBlazor's scroll manager (registered by AddMudServices in the base):
        // the deep-link scroll is a JS call, so the assertion has to be on the call, not on markup.
        Services.AddSingleton(_scroll.Object);
    }

    private static Result<PagedCollectionResult<UserNotificationDTO>> Inbox(params UserNotificationDTO[] items)
        => Result.Success(new PagedCollectionResult<UserNotificationDTO>(items, new PaginationMetadata(items.Length, 20, 1)));

    // A failed call. The message is not a resource key, so the localizer passes it through verbatim
    // and the toast text can be asserted exactly.
    private static Result<T> LoadFailure<T>(string message)
        => Result.Failure<T>(Error.Failure("Notif.Inbox.LoadFailed", message));

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

    private void VerifyOneToast(string message) =>
        _toast.Verify(t => t.Show(message, ToastSeverity.Error), Times.Once());

    private void VerifyNeverScrolled() =>
        _scroll.Verify(
            s => s.ScrollIntoViewAsync(It.IsAny<string>(), It.IsAny<ScrollBehavior>()),
            Times.Never());

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
    public void WhenTheInboxLoadFails_RaisesOneToastAndLeavesTheListAsItWas()
    {
        // The failure surface replaced a catch: a transient failure must not erase what the user is
        // already reading, so the loaded page keeps its rows and only the toast reports the fault.
        _inbox
            .Setup(x => x.GetInboxAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Inbox(Unread(1)));

        var cut = RenderUnderTest<NotificationInbox>(_ => { });
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Notice 1"));

        _inbox
            .Setup(x => x.GetInboxAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(LoadFailure<PagedCollectionResult<UserNotificationDTO>>("The inbox is unavailable."));

        _state.RequestRefresh();

        cut.WaitForAssertion(() => VerifyOneToast("The inbox is unavailable."));
        cut.Markup.Should().Contain("Notice 1", "a failed reload leaves the current list untouched");
        cut.Markup.Should().NotContain("You have no notifications.");
    }

    [Fact]
    public void WhenTheFirstInboxLoadFails_RaisesOneToast()
    {
        _inbox
            .Setup(x => x.GetInboxAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(LoadFailure<PagedCollectionResult<UserNotificationDTO>>("The inbox is unavailable."));

        var cut = RenderUnderTest<NotificationInbox>(_ => { });

        cut.WaitForAssertion(() => VerifyOneToast("The inbox is unavailable."));
        _toast.VerifyNoOtherCalls();
    }

    [Fact]
    public void ClickingMarkAsRead_MarksThatNotificationRead()
    {
        _inbox
            .Setup(x => x.GetInboxAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Inbox(Unread(7)));
        _inbox
            .Setup(x => x.MarkReadAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        _inbox
            .Setup(x => x.GetUnreadCountAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(0));

        var cut = RenderUnderTest<NotificationInbox>(_ => { });
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Notice 7"));

        cut.Find("button[aria-label=\"Mark as read\"]").Click();

        cut.WaitForAssertion(() =>
            _inbox.Verify(x => x.MarkReadAsync(7, It.IsAny<CancellationToken>()), Times.Once()));
        cut.WaitForAssertion(() => cut.FindAll(".notification-card.read").Should().ContainSingle());
    }

    [Fact]
    public void WhenMarkAsReadFails_RaisesOneToastAndSkipsTheOptimisticUpdate()
    {
        _inbox
            .Setup(x => x.GetInboxAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Inbox(Unread(7)));
        _inbox
            .Setup(x => x.MarkReadAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(Error.Failure("Notif.MarkRead.Failed", "That notification could not be marked read.")));

        var cut = RenderUnderTest<NotificationInbox>(_ => { });
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Notice 7"));

        cut.Find("button[aria-label=\"Mark as read\"]").Click();

        cut.WaitForAssertion(() => VerifyOneToast("That notification could not be marked read."));
        cut.FindAll(".notification-card.unread").Should().ContainSingle(
            "the row must not claim to be read when the server refused");
        cut.FindAll(".notification-card.read").Should().BeEmpty();
        _inbox.Verify(
            x => x.GetUnreadCountAsync(It.IsAny<CancellationToken>()),
            Times.Never(),
            "the badge refresh belongs to the success path only");
    }

    [Fact]
    public void WhenTheUnreadCountFails_AfterMarkingRead_TheSharedCountKeepsItsValue()
    {
        // A failed count means "unknown", not zero: reporting zero here is what erased the badge a
        // real-time push had just incremented.
        _inbox
            .Setup(x => x.GetInboxAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Inbox(Unread(7)));
        _inbox
            .Setup(x => x.MarkReadAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        _inbox
            .Setup(x => x.GetUnreadCountAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(LoadFailure<int>("The count is unavailable."));
        _state.SetUnreadCount(4);

        var cut = RenderUnderTest<NotificationInbox>(_ => { });
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Notice 7"));

        cut.Find("button[aria-label=\"Mark as read\"]").Click();

        cut.WaitForAssertion(() =>
            _inbox.Verify(x => x.GetUnreadCountAsync(It.IsAny<CancellationToken>()), Times.Once()));
        _state.UnreadCount.Should().Be(4);
    }

    [Fact]
    public void WhenRefreshRequested_ReloadsCurrentPage()
    {
        _inbox
            .Setup(x => x.GetInboxAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Inbox(Unread(1)));

        var cut = RenderUnderTest<NotificationInbox>(_ => { });
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Notice 1"));

        _inbox
            .Setup(x => x.GetInboxAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Inbox(Unread(1), Unread(2)));

        _state.RequestRefresh();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Notice 2"));
        _inbox.Verify(x => x.GetInboxAsync(1, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task WhenRefreshRequestedDuringAnInFlightLoad_RunsExactlyOneTrailingReload()
    {
        // The push used to be dropped outright whenever a load was already running, which left the
        // inbox showing stale contents until the next navigation.
        var firstLoad = new TaskCompletionSource<Result<PagedCollectionResult<UserNotificationDTO>>>();
        _inbox
            .SetupSequence(x => x.GetInboxAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(firstLoad.Task)
            .ReturnsAsync(Inbox(Unread(1), Unread(2)));

        var cut = RenderUnderTest<NotificationInbox>(_ => { });

        // Raised on the renderer, so the page observes the push while IsLoading is still true.
        await cut.InvokeAsync(_state.RequestRefresh);
        firstLoad.SetResult(Inbox(Unread(1)));

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("Notice 2"));
        _inbox.Verify(
            x => x.GetInboxAsync(1, It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task WhenRefreshRequestedAfterDispose_DoesNotReload()
    {
        _inbox
            .Setup(x => x.GetInboxAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Inbox(Unread(1)));

        var cut = RenderUnderTest<NotificationInbox>(_ => { });
        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("Notice 1"));

        await DisposeComponentsAsync();
        _state.RequestRefresh();

        _inbox.Verify(x => x.GetInboxAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public void ClickingMarkAllAsRead_MarksAllAndZeroesSharedCount()
    {
        _inbox
            .Setup(x => x.GetInboxAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Inbox(Unread(1), Unread(2)));
        _inbox
            .Setup(x => x.MarkAllReadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        _state.SetUnreadCount(2);

        var cut = RenderUnderTest<NotificationInbox>(_ => { });
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Mark All as Read"));

        cut.ClickButtonByText("Mark All as Read");

        cut.WaitForAssertion(() =>
            _inbox.Verify(x => x.MarkAllReadAsync(It.IsAny<CancellationToken>()), Times.Once()));
        _state.UnreadCount.Should().Be(0);
    }

    [Fact]
    public void WhenMarkAllAsReadFails_RaisesOneToastAndLeavesTheSharedCountAlone()
    {
        _inbox
            .Setup(x => x.GetInboxAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Inbox(Unread(1), Unread(2)));
        _inbox
            .Setup(x => x.MarkAllReadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(Error.Failure("Notif.MarkAllRead.Failed", "Nothing could be marked read.")));
        _state.SetUnreadCount(2);

        var cut = RenderUnderTest<NotificationInbox>(_ => { });
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Mark All as Read"));

        cut.ClickButtonByText("Mark All as Read");

        cut.WaitForAssertion(() => VerifyOneToast("Nothing could be marked read."));
        _state.UnreadCount.Should().Be(2, "the optimistic zeroing belongs to the success path only");
        cut.FindAll(".notification-card.unread").Should().HaveCount(2);
    }

    [Fact]
    public void WhenDeepLinkedNotificationIsOnThePage_HighlightsItAndScrollsToIt()
    {
        _inbox
            .Setup(x => x.GetInboxAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Inbox(Unread(1), Unread(2)));

        var cut = RenderUnderTest<NotificationInbox>(p => p.Add(c => c.Id, 2));

        cut.WaitForAssertion(() =>
            cut.FindAll(".notification-card.deep-linked").Should().ContainSingle(
                "exactly the deep-linked card carries the highlight"));
        cut.Find(".notification-card.deep-linked").Id.Should().Be("notification-2");
        cut.WaitForAssertion(() => _scroll.Verify(
            s => s.ScrollIntoViewAsync("#notification-2", ScrollBehavior.Smooth),
            Times.Once()));
        _toast.VerifyNoOtherCalls();
    }

    [Fact]
    public void WhenTheDeepLinkedNotificationIsNotOnThePage_RendersThePlainInboxSilently()
    {
        // A later page, a deleted notification, or another user's id: the route constraint already
        // proved the value is an integer, so there is nothing the user can do about the miss and no
        // toast is raised. The inbox simply renders as if the id had not been supplied.
        _inbox
            .Setup(x => x.GetInboxAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Inbox(Unread(1), Unread(2)));

        var cut = RenderUnderTest<NotificationInbox>(p => p.Add(c => c.Id, 999));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Notice 1"));
        cut.FindAll(".notification-card").Should().HaveCount(2);
        cut.FindAll(".notification-card.deep-linked").Should().BeEmpty();
        VerifyNeverScrolled();
        _toast.VerifyNoOtherCalls();
    }

    [Fact]
    public void WhenNoDeepLinkIdIsSupplied_NothingIsHighlightedAndNothingScrolls()
    {
        _inbox
            .Setup(x => x.GetInboxAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Inbox(Unread(1), Unread(2)));

        var cut = RenderUnderTest<NotificationInbox>(_ => { });

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Notice 2"));
        cut.FindAll(".notification-card.deep-linked").Should().BeEmpty(
            "the parameterless route must render byte-identically to the pre-deep-link inbox");
        VerifyNeverScrolled();
    }

    [Fact]
    public void WhenTheDeepLinkIdChanges_HighlightsAndScrollsToTheNewTarget()
    {
        // Re-navigating between two deep links reuses this component instance, so the "already
        // scrolled" latch has to clear on the parameter change or the second link would highlight
        // without ever moving the viewport.
        _inbox
            .Setup(x => x.GetInboxAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Inbox(Unread(1), Unread(2)));

        var cut = RenderUnderTest<NotificationInbox>(p => p.Add(c => c.Id, 1));
        cut.WaitForAssertion(() => _scroll.Verify(
            s => s.ScrollIntoViewAsync("#notification-1", ScrollBehavior.Smooth), Times.Once()));

        cut.Render(p => p.Add(c => c.Id, 2));

        cut.WaitForAssertion(() => _scroll.Verify(
            s => s.ScrollIntoViewAsync("#notification-2", ScrollBehavior.Smooth), Times.Once()));
        cut.Find(".notification-card.deep-linked").Id.Should().Be("notification-2");
        cut.FindAll(".notification-card.deep-linked").Should().ContainSingle();
    }

    [Fact]
    public void WhenAPushReloadsTheSamePage_TheDeepLinkScrollDoesNotRepeat()
    {
        // The highlight survives a reload, but scrolling the user's viewport again mid-read would be
        // a real annoyance, so the scroll is once per deep link, not once per load.
        _inbox
            .Setup(x => x.GetInboxAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Inbox(Unread(1), Unread(2)));

        var cut = RenderUnderTest<NotificationInbox>(p => p.Add(c => c.Id, 2));
        cut.WaitForAssertion(() => _scroll.Verify(
            s => s.ScrollIntoViewAsync("#notification-2", ScrollBehavior.Smooth), Times.Once()));

        _state.RequestRefresh();

        cut.WaitForAssertion(() => _inbox.Verify(
            x => x.GetInboxAsync(1, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(2)));
        cut.FindAll(".notification-card.deep-linked").Should().ContainSingle();
        _scroll.Verify(
            s => s.ScrollIntoViewAsync(It.IsAny<string>(), It.IsAny<ScrollBehavior>()),
            Times.Once());
    }
}

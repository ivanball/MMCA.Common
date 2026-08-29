using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.PushNotifications;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Pages.Notifications;
using MMCA.Common.UI.Services.Notifications;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Notifications;

/// <summary>
/// bUnit tests for the <see cref="NotificationList"/> history page: loaded/empty render states, the
/// failed-history surface (one toast, list left empty), and navigation to the compose page.
/// </summary>
public sealed class NotificationListTests : BunitTestBase
{
    private readonly Mock<IPushNotificationUIService> _service = new();
    private readonly Mock<IToastService> _toast = new();

    public NotificationListTests()
    {
        Services.AddSingleton(_service.Object);
        // Registered after the base class's default facade, so this wins and the page's failure
        // surface can be counted without rendering a snackbar provider. Substituting the FACADE
        // rather than MudBlazor's own ISnackbar is what lets MudSnackbarProvider keep the real
        // service it needs to render at all.
        Services.AddSingleton<IToastService>(_toast.Object);
    }

    private static Result<PagedCollectionResult<PushNotificationDTO>> History(params PushNotificationDTO[] items)
        => Result.Success(new PagedCollectionResult<PushNotificationDTO>(items, new PaginationMetadata(items.Length, 50, 1)));

    // The message is not a resource key, so the localizer passes it through verbatim and the
    // toast text can be asserted exactly.
    private static Result<PagedCollectionResult<PushNotificationDTO>> HistoryFailure(string message)
        => Result.Failure<PagedCollectionResult<PushNotificationDTO>>(
            Error.Failure("Notif.List.LoadFailed", message));

    private static PushNotificationDTO Sent(int id, string title, string status = "Sent")
        => new()
        {
            Id = id,
            Title = title,
            Body = "body",
            SentByUserId = 1,
            RecipientCount = 3,
            Status = status,
        };

    [Fact]
    public void WhenHistoryEmpty_RendersEmptyState()
    {
        _service
            .Setup(x => x.GetHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(History());

        var cut = RenderUnderTest<NotificationList>(_ => { });

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No notifications have been sent yet."));
    }

    [Fact]
    public void WhenHistoryHasItems_RendersTitlesAndStatus()
    {
        _service
            .Setup(x => x.GetHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(History(Sent(1, "Welcome aboard"), Sent(2, "Maintenance", "Failed")));

        // The populated table renders a MudTablePager whose rows-per-page MudSelect needs a popover host.
        RenderMudProviders();
        var cut = RenderUnderTest<NotificationList>(_ => { });

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Welcome aboard");
            cut.Markup.Should().Contain("Maintenance");
            cut.Markup.Should().Contain("Failed");
        });
    }

    [Fact]
    public void WhenTheHistoryLoadFails_RaisesOneToastAndLeavesTheListEmpty()
    {
        // A failed load is the one case where the empty state is a lie, so the toast carries the
        // API's own wording rather than the page inventing a message of its own.
        _service
            .Setup(x => x.GetHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(HistoryFailure("The notification history is unavailable."));

        var cut = RenderUnderTest<NotificationList>(_ => { });

        cut.WaitForAssertion(() => _toast.Verify(
            t => t.Show("The notification history is unavailable.", ToastSeverity.Error),
            Times.Once()));
        _toast.VerifyNoOtherCalls();
        cut.Markup.Should().Contain("No notifications have been sent yet.");
    }

    [Fact]
    public void ClickingSendNew_NavigatesToComposePage()
    {
        // A failed history load must not disable the one action the page still offers.
        _service
            .Setup(x => x.GetHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(HistoryFailure("The notification history is unavailable."));
        var nav = Services.GetRequiredService<NavigationManager>();

        var cut = RenderUnderTest<NotificationList>(_ => { });
        cut.ClickButtonByText("Send New Notification");

        nav.Uri.Should().EndWith("/notifications/send");
    }
}

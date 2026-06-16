using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.PushNotifications;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Pages.Notifications;
using MMCA.Common.UI.Services.Notifications;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Notifications;

/// <summary>
/// bUnit tests for the <see cref="NotificationList"/> history page — loaded/empty render states and
/// navigation to the compose page.
/// </summary>
public sealed class NotificationListTests : BunitTestBase
{
    private readonly Mock<IPushNotificationUIService> _service = new();

    public NotificationListTests() => Services.AddSingleton(_service.Object);

    private static PagedCollectionResult<PushNotificationDTO> History(params PushNotificationDTO[] items)
        => new(items, new PaginationMetadata(items.Length, 50, 1));

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
    public void ClickingSendNew_NavigatesToComposePage()
    {
        _service
            .Setup(x => x.GetHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PagedCollectionResult<PushNotificationDTO>?)null);
        var nav = Services.GetRequiredService<NavigationManager>();

        var cut = RenderUnderTest<NotificationList>(_ => { });
        cut.ClickButtonByText("Send New Notification");

        nav.Uri.Should().EndWith("/notifications/send");
    }
}

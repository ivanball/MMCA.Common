using System.Security.Claims;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Notifications;
using MMCA.Common.Shared.Notifications.PushNotifications;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Pages.Notifications;
using MMCA.Common.UI.Services.Notifications;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Notifications;

/// <summary>
/// bUnit tests for the <see cref="NotificationList"/> history page: loaded/empty render states, the
/// failed-history surface (one toast plus the inline error and Retry, never the empty state), server
/// paging over the whole history, and navigation to the compose page.
/// </summary>
public sealed class NotificationListTests : BunitTestBase
{
    /// <summary>An account holding the permission the page and its navigation entry are gated on.</summary>
    private static readonly ClaimsPrincipal Manager = new(new ClaimsIdentity(
        [new Claim(AuthClaimTypes.Subject, "1"), new Claim(AuthClaimTypes.Permission, NotificationPermissions.Manage)],
        authenticationType: "TestAuth"));

    /// <summary>A signed-in account without it, which the history endpoint would refuse.</summary>
    private static readonly ClaimsPrincipal Attendee = new(new ClaimsIdentity(
        [new Claim(AuthClaimTypes.Subject, "2")],
        authenticationType: "TestAuth"));

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

        var cut = RenderAs<NotificationList>(Manager, _ => { });

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
        var cut = RenderAs<NotificationList>(Manager, _ => { });

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Welcome aboard");
            cut.Markup.Should().Contain("Maintenance");
            cut.Markup.Should().Contain("Failed");
        });
    }

    [Fact]
    public void WhenTheHistoryLoadFails_RaisesOneToastAndShowsTheErrorWithRetry_NotTheEmptyState()
    {
        // A failed load is the one case where the empty state is a lie, so the toast carries the
        // API's own wording rather than the page inventing a message of its own.
        _service
            .Setup(x => x.GetHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(HistoryFailure("The notification history is unavailable."));

        var cut = RenderAs<NotificationList>(Manager, _ => { });

        cut.WaitForAssertion(() => _toast.Verify(
            t => t.Show("The notification history is unavailable.", ToastSeverity.Error),
            Times.Once()));
        _toast.VerifyNoOtherCalls();

        // L190: the empty-state alert used to render here once the toast expired, which tells an
        // operator that nothing was ever sent. The failure stays on screen with a way to retry.
        cut.Markup.Should().NotContain("No notifications have been sent yet.");
        cut.Find("[role='alert']").TextContent.Should().Contain("The notification history is unavailable.");
        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Retry");
    }

    [Fact]
    public async Task Retry_AfterAFailedLoad_LoadsTheHistory()
    {
        _service
            .Setup(x => x.GetHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(HistoryFailure("The notification history is unavailable."));
        RenderMudProviders();
        var cut = RenderAs<NotificationList>(Manager, _ => { });
        await cut.WaitForAssertionAsync(() => cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Retry"));

        _service
            .Setup(x => x.GetHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(History(Sent(1, "Welcome aboard")));
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Retry").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("Welcome aboard"));
        cut.FindAll("[role='alert']").Should().BeEmpty("a successful retry clears the error");
    }

    // L191: the history used to be one 50-row read paged client-side, so every older send was
    // unreachable and nothing said so. The pager now pages the server's own total.
    [Fact]
    public async Task WhenTheHistoryHasMoreThanOnePage_AnOlderPageIsReadFromTheServer()
    {
        _service
            .Setup(x => x.GetHistoryAsync(1, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, int pageSize, CancellationToken _) => Result.Success(
                new PagedCollectionResult<PushNotificationDTO>([Sent(1, "Newest send")], new PaginationMetadata(pageSize * 2 + 1, pageSize, 1))));
        _service
            .Setup(x => x.GetHistoryAsync(3, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, int pageSize, CancellationToken _) => Result.Success(
                new PagedCollectionResult<PushNotificationDTO>([Sent(99, "Oldest send")], new PaginationMetadata(pageSize * 2 + 1, pageSize, 3))));
        RenderMudProviders();
        var cut = RenderAs<NotificationList>(Manager, _ => { });
        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("Newest send"));

        await cut.FindAll(".mud-pagination button").Single(b => b.TextContent.Trim() == "3")
            .ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("Oldest send"));
        _service.Verify(x => x.GetHistoryAsync(3, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public void WhenTheAccountLacksTheManagePermission_RendersAccessDenied_AndNeverLoadsTheHistory()
    {
        // The page is reachable by URL for any signed-in account; without notifications:manage it must
        // show the shell's 403 view rather than an empty history plus the API's refusal toast.
        var cut = RenderAs<NotificationList>(Attendee, _ => { });

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Access Denied"));
        cut.Markup.Should().NotContain("Send New Notification");
        _service.Verify(
            x => x.GetHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never());
        _toast.VerifyNoOtherCalls();
    }

    [Fact]
    public void ClickingSendNew_NavigatesToComposePage()
    {
        // A failed history load must not disable the one action the page still offers.
        _service
            .Setup(x => x.GetHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(HistoryFailure("The notification history is unavailable."));
        var nav = Services.GetRequiredService<NavigationManager>();

        var cut = RenderAs<NotificationList>(Manager, _ => { });
        cut.ClickButtonByText("Send New Notification");

        nav.Uri.Should().EndWith("/notifications/send");
    }
}

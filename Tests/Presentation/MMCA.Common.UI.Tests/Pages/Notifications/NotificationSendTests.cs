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
using MudBlazor;

namespace MMCA.Common.UI.Tests.Pages.Notifications;

/// <summary>
/// bUnit tests for the <see cref="NotificationSend"/> compose page: form validation gating,
/// successful submit wiring (service call + snackbar + navigation), the failed-send surface (one
/// snackbar, no navigation, the wording repeated inline by the shared <c>ErrorSummary</c>), and
/// cancel navigation.
/// </summary>
public sealed class NotificationSendTests : BunitTestBase
{
    private readonly Mock<IPushNotificationUIService> _service = new();
    private readonly Mock<ISnackbar> _snackbar = new();

    public NotificationSendTests()
    {
        Services.AddSingleton(_service.Object);
        // Registered after the base class's AddMudServices, so this wins and the page's snackbar
        // surface can be counted without rendering a snackbar provider.
        Services.AddSingleton<ISnackbar>(_snackbar.Object);
    }

    private static Result<PushNotificationDTO> Accepted(int recipientCount)
        => Result.Success(new PushNotificationDTO
        {
            Id = 1,
            Title = "Hello",
            Body = "World body",
            SentByUserId = 1,
            RecipientCount = recipientCount,
            Status = "Sent",
        });

    // The messages are not resource keys, so the localizer passes them through verbatim and both the
    // snackbar text and the inline summary can be asserted exactly.
    private static Result<PushNotificationDTO> SendFailure(params string[] messages)
        => Result.Failure<PushNotificationDTO>(
            [.. messages.Select(message => Error.Failure("Notif.Send.Failed", message))]);

    [Fact]
    public void SubmittingEmptyForm_ShowsValidationAndDoesNotCallService()
    {
        var cut = RenderUnderTest<NotificationSend>(_ => { });

        cut.ClickButtonByText("Send to All Recipients");

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Title is required."));
        _service.Verify(
            x => x.SendAsync(It.IsAny<SendPushNotificationRequest>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    [Fact]
    public void RequiredFields_KeepTheirAriaRequiredAffordance()
    {
        // The markup no longer hard-codes Required: it is read off the model's own annotations, so
        // this asserts the accessibility affordance survived the move to the validation adapter.
        var cut = RenderUnderTest<NotificationSend>(_ => { });

        cut.Find("input").OuterHtml.Should().Contain("aria-required=\"true\"");
        cut.Find("textarea").OuterHtml.Should().Contain("aria-required=\"true\"");
    }

    [Fact]
    public void SubmittingEmptyForm_ShowsExactlyOneMessagePerField()
    {
        // MudBlazor's built-in required text must not stack on top of the model's message: the
        // adapter is the only source of the wording.
        var cut = RenderUnderTest<NotificationSend>(_ => { });

        cut.ClickButtonByText("Send to All Recipients");

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Title is required."));
        cut.Markup.Should().NotContain(">Required<");
    }

    [Fact]
    public void SubmittingAnOverlongTitle_ShowsTheMaxLengthMessageAndDoesNotCallService()
    {
        // The length rule lives only on NotificationSendModel, which reads its number off the shared
        // request contract: the markup declares no MaxLength rule, so seeing this message proves the
        // model's DataAnnotations are what the field validates.
        var cut = RenderUnderTest<NotificationSend>(_ => { });

        cut.Find("input").Input(new string('x', SendPushNotificationRequest.TitleMaxLength + 1));
        cut.Find("textarea").Input("World body");
        cut.ClickButtonByText("Send to All Recipients");

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Title cannot exceed 200 characters."));
        _service.Verify(
            x => x.SendAsync(It.IsAny<SendPushNotificationRequest>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    [Fact]
    public void SubmittingValidForm_CallsServiceAndNavigatesToList()
    {
        _service
            .Setup(x => x.SendAsync(It.IsAny<SendPushNotificationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Accepted(recipientCount: 10));
        var nav = Services.GetRequiredService<NavigationManager>();

        var cut = RenderUnderTest<NotificationSend>(_ => { });
        cut.Find("input").Input("Hello");
        cut.Find("textarea").Input("World body");
        cut.ClickButtonByText("Send to All Recipients");

        cut.WaitForAssertion(() => _service.Verify(
            x => x.SendAsync(
                It.Is<SendPushNotificationRequest>(r => r.Title == "Hello" && r.Body == "World body"),
                It.IsAny<CancellationToken>()),
            Times.Once()));
        nav.Uri.Should().EndWith("/notifications");

        // The success cue names how many people it reached, which is the only feedback the user gets
        // once the page has navigated away.
        _snackbar.Verify(
            s => s.Add(
                "Notification sent to 10 recipients.",
                Severity.Success,
                It.IsAny<Action<SnackbarOptions>>(),
                It.IsAny<string>()),
            Times.Once());
    }

    [Fact]
    public void WhenTheSendFails_StaysOnThePageAndRaisesOneSnackbar()
    {
        _service
            .Setup(x => x.SendAsync(It.IsAny<SendPushNotificationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SendFailure("The notification service is unavailable."));
        var nav = Services.GetRequiredService<NavigationManager>();
        var startingUri = nav.Uri;

        var cut = RenderUnderTest<NotificationSend>(_ => { });
        cut.Find("input").Input("Hello");
        cut.Find("textarea").Input("World body");
        cut.ClickButtonByText("Send to All Recipients");

        cut.WaitForAssertion(() => _snackbar.Verify(
            s => s.Add(
                "The notification service is unavailable.",
                Severity.Error,
                It.IsAny<Action<SnackbarOptions>>(),
                It.IsAny<string>()),
            Times.Once()));
        nav.Uri.Should().Be(startingUri, "a failed send must not throw away what the user typed");
    }

    [Fact]
    public void WhenTheSendFails_TheFailureIsRepeatedInlineByTheErrorSummary()
    {
        // The snackbar times out; the composed form does not. The inline summary is what the user
        // still has to read once the toast is gone.
        _service
            .Setup(x => x.SendAsync(It.IsAny<SendPushNotificationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SendFailure("The notification service is unavailable."));

        var cut = RenderUnderTest<NotificationSend>(_ => { });
        cut.Find("input").Input("Hello");
        cut.Find("textarea").Input("World body");
        cut.ClickButtonByText("Send to All Recipients");

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("The notification service is unavailable."));
        cut.FindAll(".mud-alert").Should().NotBeEmpty();
    }

    [Fact]
    public void WhenTheSendFailsWithSeveralErrors_TheSummaryListsThemSeparately()
    {
        // Several independent failures must stay readable (and announceable) as several items.
        _service
            .Setup(x => x.SendAsync(It.IsAny<SendPushNotificationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SendFailure("No recipients are registered.", "The notification service is unavailable."));

        var cut = RenderUnderTest<NotificationSend>(_ => { });
        cut.Find("input").Input("Hello");
        cut.Find("textarea").Input("World body");
        cut.ClickButtonByText("Send to All Recipients");

        cut.WaitForAssertion(() => cut.FindAll(".mmca-error-summary-list li").Should().HaveCount(2));
        cut.Markup.Should().Contain("No recipients are registered.");
        cut.Markup.Should().Contain("The notification service is unavailable.");
    }

    [Fact]
    public void ClickingCancel_NavigatesToListWithoutSending()
    {
        var nav = Services.GetRequiredService<NavigationManager>();

        var cut = RenderUnderTest<NotificationSend>(_ => { });
        cut.ClickButtonByText("Cancel");

        nav.Uri.Should().EndWith("/notifications");
        _service.Verify(
            x => x.SendAsync(It.IsAny<SendPushNotificationRequest>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }
}

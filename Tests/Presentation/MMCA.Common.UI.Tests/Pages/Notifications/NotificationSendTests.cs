using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Notifications.PushNotifications;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Pages.Notifications;
using MMCA.Common.UI.Services.Notifications;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Notifications;

/// <summary>
/// bUnit tests for the <see cref="NotificationSend"/> compose page — form validation gating,
/// successful submit wiring (service call + navigation), and cancel navigation.
/// </summary>
public sealed class NotificationSendTests : BunitTestBase
{
    private readonly Mock<IPushNotificationUIService> _service = new();

    public NotificationSendTests() => Services.AddSingleton(_service.Object);

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
        // The length rule lives only on NotificationSendModel: the markup declares no MaxLength rule,
        // so seeing this message proves the model's DataAnnotations are what the field validates.
        var cut = RenderUnderTest<NotificationSend>(_ => { });

        cut.Find("input").Input(new string('x', NotificationSendModel.TitleMaxLength + 1));
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
            .ReturnsAsync(new PushNotificationDTO
            {
                Id = 1,
                Title = "Hello",
                Body = "World body",
                SentByUserId = 1,
                RecipientCount = 10,
                Status = "Sent",
            });
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

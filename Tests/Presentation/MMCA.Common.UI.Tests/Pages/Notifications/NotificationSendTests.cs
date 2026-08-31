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
/// bUnit tests for the <see cref="NotificationSend"/> compose page: form validation gating,
/// successful submit wiring (service call + toast + navigation), the failed-send surface (one
/// toast, no navigation, the wording repeated inline by the shared <c>ErrorSummary</c>), the
/// auto-target caption, and cancel navigation.
/// </summary>
public sealed class NotificationSendTests : BunitTestBase
{
    /// <summary>
    /// Scope provider that supplies a display name, standing in for a scoped application. It relies
    /// on the interface's default implementation of nothing: the display-name member is overridden
    /// here while the key member is implemented explicitly, which is the shape a real app has.
    /// </summary>
    private sealed class NamedScopeProvider(string? displayName) : INotificationScopeProvider
    {
        public Task<string?> GetCurrentScopeKeyAsync(CancellationToken ct = default) =>
            Task.FromResult<string?>("event:2");

        public Task<string?> GetCurrentScopeDisplayNameAsync(CancellationToken ct = default) =>
            Task.FromResult(displayName);
    }

    private readonly Mock<IPushNotificationUIService> _service = new();
    private readonly Mock<IToastService> _toast = new();

    public NotificationSendTests()
    {
        Services.AddSingleton(_service.Object);
        // Registered after the base class's default facade, so this wins and the page's toast
        // surface can be counted without rendering a snackbar provider.
        Services.AddSingleton<IToastService>(_toast.Object);
        // The framework default. A test that needs a captioned scope registers its own provider
        // after this one, and the later registration is the one resolved.
        Services.AddSingleton<INotificationScopeProvider>(new NullNotificationScopeProvider());
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
    // toast text and the inline summary can be asserted exactly.
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

    // ── Auto-target caption ──
    [Fact]
    public void WithAScopedProvider_CaptionsTheAutoAppliedTarget()
    {
        // The send is scoped automatically by the notification service; without this caption the
        // operator composes a broadcast with no statement of who receives it.
        Services.AddSingleton<INotificationScopeProvider>(new NamedScopeProvider("ADC 2026"));

        var cut = RenderUnderTest<NotificationSend>(_ => { });

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Targeting: ADC 2026"));
    }

    [Fact]
    public void WithAnUnscopedProvider_RendersNoCaption()
    {
        // The null provider is the framework default, and an unscoped app must not gain a caption
        // with nothing in it.
        var cut = RenderUnderTest<NotificationSend>(_ => { });

        cut.Markup.Should().NotContain("Targeting:");
        cut.FindAll(".mmca-send-scope").Should().BeEmpty();
    }

    [Fact]
    public void WithAScopedProviderThatHasNoDisplayName_RendersNoCaption()
    {
        // A provider that scopes but cannot name the scope fails closed to no caption rather than
        // printing a bare label.
        Services.AddSingleton<INotificationScopeProvider>(new NamedScopeProvider(displayName: null));

        var cut = RenderUnderTest<NotificationSend>(_ => { });

        cut.FindAll(".mmca-send-scope").Should().BeEmpty();
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
        _toast.Verify(t => t.Success("Notification sent to 10 recipients."), Times.Once());
    }

    [Fact]
    public void WhenTheSendFails_StaysOnThePageAndRaisesOneToast()
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

        cut.WaitForAssertion(() => _toast.Verify(
            t => t.Show("The notification service is unavailable.", ToastSeverity.Error),
            Times.Once()));
        nav.Uri.Should().Be(startingUri, "a failed send must not throw away what the user typed");
    }

    [Fact]
    public void WhenTheSendFails_TheFailureIsRepeatedInlineByTheErrorSummary()
    {
        // The toast times out; the composed form does not. The inline summary is what the user
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

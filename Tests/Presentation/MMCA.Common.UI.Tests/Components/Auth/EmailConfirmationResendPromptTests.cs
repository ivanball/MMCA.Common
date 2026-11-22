using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Components.Auth;
using MMCA.Common.UI.Services.Auth;
using Moq;

namespace MMCA.Common.UI.Tests.Components.Auth;

/// <summary>
/// The resend prompt (design 2.4): one click asks for a fresh confirmation link for the trimmed
/// address; a success shows the sent status and disables the button for the rest of the prompt's life
/// (one send per prompt); a refusal, such as the throttle's 429, shows the localized words as an alert
/// and leaves the button usable; disposing the prompt cancels a send still in flight.
/// </summary>
public sealed class EmailConfirmationResendPromptTests : BunitTestBase
{
    private const string ResendButton = "[data-testid='email-confirmation-resend']";

    private readonly Mock<IEmailConfirmationUIService> _confirmation = new();

    public EmailConfirmationResendPromptTests() => Services.AddSingleton(_confirmation.Object);

    [Fact]
    public void Click_SendsTheTrimmedAddress_Once()
    {
        Accepts();
        var cut = RenderPrompt("  ada@example.com  ");

        cut.Find(ResendButton).Click();

        cut.WaitForAssertion(() => _confirmation.Verify(
            x => x.ResendEmailConfirmationAsync("ada@example.com", It.IsAny<CancellationToken>()),
            Times.Once()));
    }

    [Fact]
    public void ASuccess_ShowsTheSentStatus_AndDisablesTheButton()
    {
        Accepts();
        var cut = RenderPrompt("ada@example.com");

        cut.Find(ResendButton).Click();

        cut.WaitForAssertion(() =>
        {
            var sent = cut.Find("[data-testid='email-confirmation-resend-sent']");
            sent.GetAttribute("role").Should().Be("status");
            sent.GetAttribute("aria-live").Should().Be("polite");
        });
        cut.Find(ResendButton).HasAttribute("disabled").Should().BeTrue("one send per prompt; the server throttles the rest");
        cut.FindAll("[role='alert']").Should().BeEmpty();
    }

    [Fact]
    public void ASecondClickAfterASuccess_SendsNothingMore()
    {
        Accepts();
        var cut = RenderPrompt("ada@example.com");
        cut.Find(ResendButton).Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid='email-confirmation-resend-sent']"));

        cut.Find(ResendButton).Click();

        _confirmation.Verify(
            x => x.ResendEmailConfirmationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once());
    }

    [Fact]
    public void ASuccess_RaisesOnSent()
    {
        Accepts();
        var sent = 0;
        var cut = Render<EmailConfirmationResendPrompt>(p => p
            .Add(x => x.Email, "ada@example.com")
            .Add(x => x.OnSent, () => sent++));

        cut.Find(ResendButton).Click();

        cut.WaitForAssertion(() => sent.Should().Be(1));
    }

    [Fact]
    public void AThrottledRefusal_ShowsTheLocalizedAlert_AndKeepsTheButtonEnabled()
    {
        // A 429 reaches the UI as a failure whose message is already localized by the API.
        _confirmation
            .Setup(x => x.ResendEmailConfirmationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(Error.Failure("Http.429", "Too many requests. Try again in a few minutes.")));
        var cut = RenderPrompt("ada@example.com");

        cut.Find(ResendButton).Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid='email-confirmation-resend-error']").TextContent.Trim()
            .Should().Be("Too many requests. Try again in a few minutes."));
        cut.Find("[data-testid='email-confirmation-resend-error']").GetAttribute("role").Should().Be("alert");
        cut.Find(ResendButton).HasAttribute("disabled").Should().BeFalse("a refused send can be tried again");
        cut.FindAll("[data-testid='email-confirmation-resend-sent']").Should().BeEmpty();
    }

    [Fact]
    public async Task Disposal_CancelsASendStillInFlight()
    {
        var pending = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken? token = null;
        _confirmation
            .Setup(x => x.ResendEmailConfirmationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((_, ct) => token = ct)
            .Returns(pending.Task);
        var cut = RenderPrompt("ada@example.com");
        // Not awaited yet: the click completes only once the held-open send does.
        var click = cut.Find(ResendButton).ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await cut.WaitForAssertionAsync(() => token.Should().NotBeNull());

        await DisposeComponentsAsync();

        token!.Value.IsCancellationRequested.Should().BeTrue("disposing the prompt cancels its send");
        pending.SetCanceled(token.Value);
        await click.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);
    }

    private void Accepts() =>
        _confirmation
            .Setup(x => x.ResendEmailConfirmationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

    private IRenderedComponent<EmailConfirmationResendPrompt> RenderPrompt(string email) =>
        Render<EmailConfirmationResendPrompt>(p => p.Add(x => x.Email, email));
}

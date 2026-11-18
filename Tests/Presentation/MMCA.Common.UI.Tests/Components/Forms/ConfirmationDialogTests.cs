using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Common.Interfaces;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Components.Forms;

/// <summary>
/// B2: <see cref="IAppDialogService.ConfirmAsync(string, string, string, string)"/> opens the same
/// Cancel-first dialog <c>DeleteConfirmation</c> does, instead of a MudMessageBox whose focus trap
/// started on the confirming button (so a stray Enter confirmed a lock, a sign-out or a deleted
/// passkey). Pins the button order, the first-child initial focus, Escape answering
/// <see langword="false"/>, and the destructive overload's error-colored confirm button. The real
/// browser half of the focus (which element actually holds it) is the E2E tier's job.
/// </summary>
public sealed class ConfirmationDialogTests : BunitTestBase
{
    private const string Title = "Lock account?";
    private const string Message = "grace@example.com will be signed out.";

    [Fact]
    public async Task ConfirmAsync_RendersCancelBeforeTheConfirmButton()
    {
        var providers = RenderMudProviders();

        _ = await OpenAsync(providers, destructive: null);

        providers.Dialog.FindAll(".mud-dialog-actions button")
            .Select(button => button.TextContent.Trim())
            .Should().Equal(["Cancel", "Lock"], "the declining button comes first, so it is the one the focus trap starts on");
    }

    [Fact]
    public async Task ConfirmAsync_StartsTheFocusTrapOnTheFirstChild_AndKeepsTheMessageBoxClass()
    {
        var providers = RenderMudProviders();

        _ = await OpenAsync(providers, destructive: true);

        providers.Dialog.FindComponents<MudFocusTrap>()
            .Should().NotBeEmpty("the dialog traps focus (precondition)")
            .And.OnlyContain(
                trap => trap.Instance.DefaultFocus == DefaultFocus.FirstChild,
                "initial focus lands on Cancel, never on the confirming button");
        providers.Dialog.FindAll(".mud-message-box").Should().NotBeEmpty("existing mud-message-box selectors keep matching");
    }

    [Theory]
    [InlineData(null, "mud-button-filled-primary")]
    [InlineData(false, "mud-button-filled-primary")]
    [InlineData(true, "mud-button-filled-error")]
    public async Task ConfirmButtonColor_FollowsTheDestructiveFlag(bool? destructive, string expectedClass)
    {
        var providers = RenderMudProviders();

        _ = await OpenAsync(providers, destructive);

        providers.Dialog.Find("button.mud-message-box__yes-button").ClassList.Should().Contain(expectedClass);
    }

    [Fact]
    public async Task ClickingConfirm_AnswersTrue()
    {
        var providers = RenderMudProviders();
        var answer = await OpenAsync(providers, destructive: true);

        providers.Dialog.ClickButtonByText("Lock");

        (await answer).Should().BeTrue();
    }

    [Fact]
    public async Task ClickingCancel_AnswersFalse()
    {
        var providers = RenderMudProviders();
        var answer = await OpenAsync(providers, destructive: true);

        providers.Dialog.ClickButtonByText("Cancel");

        (await answer).Should().BeFalse();
    }

    [Fact]
    public async Task Escape_ClosesTheDialog_AndAnswersFalse()
    {
        var providers = RenderMudProviders();
        var answer = await OpenAsync(providers, destructive: true);

        await DeleteConfirmationTests.PressEscapeInTheDialogAsync(JSInterop, providers.Dialog);

        var finished = await Task.WhenAny(answer, Task.Delay(TimeSpan.FromSeconds(3), Xunit.TestContext.Current.CancellationToken));
        finished.Should().BeSameAs(answer, "Escape must close the confirmation instead of being ignored");
        (await answer).Should().BeFalse("dismissing with Escape declines, never confirms");
        providers.Dialog.FindAll(".mud-message-box").Should().BeEmpty("the dialog is gone");
    }

    /// <summary>
    /// Opens the prompt through the registered <see cref="IAppDialogService"/> without awaiting the
    /// answer (it completes only once a button is clicked), and waits for the dialog to render.
    /// A <see langword="null"/> <paramref name="destructive"/> uses the four-argument overload.
    /// </summary>
    private async Task<Task<bool>> OpenAsync(MudProviderHandles providers, bool? destructive)
    {
        var dialogs = Services.GetRequiredService<IAppDialogService>();
        Task<bool>? answer = null;
        await providers.Dialog.InvokeAsync(() =>
        {
            answer = destructive is { } flag
                ? dialogs.ConfirmAsync(Title, Message, "Lock", "Cancel", flag)
                : dialogs.ConfirmAsync(Title, Message, "Lock", "Cancel");
        });
        await providers.Dialog.WaitForAssertionAsync(() => providers.Dialog.HasText(Message).Should().BeTrue());
        return answer!;
    }
}

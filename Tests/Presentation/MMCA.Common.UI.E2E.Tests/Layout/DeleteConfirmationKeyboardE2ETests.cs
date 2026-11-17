using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.UI.E2E.Tests.Layout;

/// <summary>
/// X-08 (Store run 1): the shared delete confirmation opened with keyboard focus on the destructive
/// Delete button, so a stray Enter deleted the record, and it ignored Escape. In a real browser the
/// dialog must open with focus on the non-destructive Cancel button, and Escape must close it
/// without deleting (the gallery's <c>/components</c> page hosts the shared
/// <c>DeleteConfirmation</c> behind its "Delete sample" button).
/// </summary>
public sealed class DeleteConfirmationKeyboardE2ETests : GalleryAxeTestBase
{
    public DeleteConfirmationKeyboardE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright, gallery)
    {
    }

    [Fact]
    public async Task OpeningTheDialog_PutsInitialFocusOnCancel()
    {
        await OpenDeleteConfirmationAsync();

        var dialog = Page.GetByRole(AriaRole.Dialog);
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel" })).ToBeFocusedAsync();
        await Expect(Page.GetByTestId("confirm-delete")).Not.ToBeFocusedAsync();
    }

    [Fact]
    public async Task Escape_ClosesTheDialog()
    {
        await OpenDeleteConfirmationAsync();
        var confirm = Page.GetByTestId("confirm-delete");

        // The dialog closes on Escape through its own key handler, so the key only reaches it once the
        // focus trap has moved focus inside. WebKit does not focus a clicked button, so a key pressed
        // before that lands on the document and is lost; wait for the initial focus first.
        await Expect(Page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Cancel" })).ToBeFocusedAsync();

        await Page.Keyboard.PressAsync("Escape");

        await Expect(confirm).Not.ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0);
    }

    /// <summary>
    /// B2: <c>IAppDialogService.ConfirmAsync</c> opens the same Cancel-first dialog. The gallery's
    /// <c>/components</c> page hosts an <c>UnsavedChangesGuard</c>, whose prompt is a ConfirmAsync:
    /// mark the page dirty, start an in-app navigation, and the prompt must open on "Stay" (its
    /// declining button), and Escape must keep the user on the page.
    /// </summary>
    [Fact]
    public async Task UnsavedChangesPrompt_PutsInitialFocusOnStay_AndEscapeStaysOnThePage()
    {
        await Page.GotoAndWaitForBlazorAsync("/components");
        await Page.GetByTestId("toggle-dirty").ClickAsync();
        await Expect(Page.GetByText("Dirty state: unsaved changes")).ToBeVisibleAsync();

        // An in-app navigation, so the guard's LocationChanging handler runs (a full load would not).
        await Page.EvaluateAsync("() => Blazor.navigateTo('/grid')");

        var dialog = Page.GetByRole(AriaRole.Dialog);
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Stay" })).ToBeFocusedAsync();
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Leave" })).Not.ToBeFocusedAsync();

        await Page.Keyboard.PressAsync("Escape");

        await Expect(Page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0);
        await Expect(Page.GetByText("Dirty state: unsaved changes")).ToBeVisibleAsync();
        Assert.EndsWith("/components", Page.Url, StringComparison.Ordinal);
    }

    private async Task OpenDeleteConfirmationAsync()
    {
        await Page.GotoAndWaitForBlazorAsync("/components");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Delete sample" }).ClickAsync();
        await Expect(Page.GetByTestId("confirm-delete")).ToBeVisibleAsync();
    }
}

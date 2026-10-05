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

        await Page.Keyboard.PressAsync("Escape");

        await Expect(confirm).Not.ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0);
    }

    private async Task OpenDeleteConfirmationAsync()
    {
        await Page.GotoAndWaitForBlazorAsync("/components");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Delete sample" }).ClickAsync();
        await Expect(Page.GetByTestId("confirm-delete")).ToBeVisibleAsync();
    }
}

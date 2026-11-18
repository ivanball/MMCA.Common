using AwesomeAssertions;
using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.Testing.E2E.PageObjects;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.Testing.E2E.Workflows.Identity;

/// <summary>
/// Account self-deletion journey (the right to erasure, ADR-005), lifted from MMCA.ADC's
/// <c>AccountDeletionTests</c>. A freshly registered user deletes their own account from the profile
/// page through the shared confirmation dialog, is signed out, sees the deletion confirmed after the
/// forced reload, and can no longer sign in (the account is soft-deleted and anonymized, so login
/// answers the generic credentials error).
/// <para>
/// The journey runs the real UI against the real erasure endpoint, so it catches what a mocked
/// component test and an API test each miss on their own: a moved route, a same-origin proxy that
/// drops the call, or a render mode that never wires the button. The section, button and message
/// texts are virtual because each app words its delete card differently; the defaults are ADC's.
/// </para>
/// </summary>
public abstract class AccountDeletionTestsBase : E2ETestBase
{
    protected AccountDeletionTestsBase(PlaywrightFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// Text that identifies the card holding the delete button (matched as a case-insensitive
    /// substring of the card's text, so the card heading is enough). Default: ADC's "Delete Account".
    /// </summary>
    protected virtual string DeleteSectionText => "Delete Account";

    /// <summary>
    /// The accessible name of the delete button, inside the card AND on the dialog's confirm button.
    /// Matched case-insensitively. Default: "Delete My Account".
    /// </summary>
    protected virtual string DeleteButtonName => "Delete My Account";

    /// <summary>The toast shown once, after the forced reload, when the deletion succeeded.</summary>
    protected virtual string AccountDeletedMessage => "Account deleted successfully.";

    /// <summary>The confirmation dialog's title to assert, or null to skip the check.</summary>
    protected virtual string? ConfirmDialogTitle => null;

    /// <summary>The confirmation dialog's message to assert, or null to skip the check.</summary>
    protected virtual string? ConfirmDialogMessage => null;

    /// <summary>
    /// Whether the journey first answers the dialog with Cancel and checks that nothing changed (the
    /// user is still signed in and the card is still there) before deleting for real. Off by default.
    /// </summary>
    protected virtual bool CancelsBeforeConfirming => false;

    /// <summary>The card holding the delete button. Override when the card text alone is ambiguous.</summary>
    protected virtual ILocator DeleteSection =>
        Page.Locator(".mud-card").Filter(new() { HasText = DeleteSectionText });

    /// <summary>
    /// Opens the page that offers account deletion, signed in as the user just registered. Default: the
    /// framework's profile page (<c>/profile</c>).
    /// </summary>
    /// <returns>A task that completes once the page has loaded.</returns>
    protected virtual async Task OpenAccountDeletionPageAsync()
    {
        await new ProfilePage(Page).GotoAsync().ConfigureAwait(false);
        await Page.WaitForLoadStateAsync(LoadState.Load).ConfigureAwait(false);
    }

    [Fact]
    public async Task DeleteAccount_ShouldLogOutAndPreventRelogin()
    {
        // Arrange: a throwaway account, signed in, on the page that offers deletion.
        var (email, password) = await RegisterNewUserAsync().ConfigureAwait(false);
        await OpenAccountDeletionPageAsync().ConfigureAwait(false);
        var logout = Page.GetByRole(AriaRole.Button, new() { Name = "Logout" });

        if (CancelsBeforeConfirming)
        {
            var cancelled = await OpenConfirmationAsync().ConfigureAwait(false);
            await cancelled.Locator(".mud-message-box__cancel-button").ClickAsync().ConfigureAwait(false);
            await Expect(cancelled).ToBeHiddenAsync(new() { Timeout = 10_000 }).ConfigureAwait(false);
            await Expect(logout).ToBeVisibleAsync().ConfigureAwait(false);
            await Expect(DeleteSection).ToBeVisibleAsync().ConfigureAwait(false);
        }

        // Act: delete and confirm. The trigger and the dialog's confirm button share the accessible
        // name, so each is scoped: the trigger to its card, the confirm to the shared confirmation's
        // "confirm-action" button.
        var dialog = await OpenConfirmationAsync().ConfigureAwait(false);
        await dialog.GetByTestId("confirm-action").ClickAsync().ConfigureAwait(false);

        // Assert: deletion signs the user out (the page navigates to "/" with a forced load after
        // revoking the session), so the Logout control disappears.
        await Expect(logout).Not.ToBeVisibleAsync(new() { Timeout = 15_000 }).ConfigureAwait(false);

        // Assert: the confirmation survives the forced reload. The page parks it as a pending toast
        // before signing out and the shared layout shows it on the next page load (home, or login
        // when the sign-out's redirect wins the race, as on Firefox).
        await Expect(Page.Locator(".mud-snackbar").Filter(new() { HasText = AccountDeletedMessage }))
            .ToBeVisibleAsync(new() { Timeout = 15_000 }).ConfigureAwait(false);

        // Assert: the deleted account can no longer authenticate. LoginAsync surfaces the generic
        // credentials error as an InvalidOperationException.
        var signIn = () => LoginAsync(email, password);
        await signIn.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
    }

    private async Task<ILocator> OpenConfirmationAsync()
    {
        await DeleteSection.GetByRole(AriaRole.Button, new() { Name = DeleteButtonName }).ClickAsync().ConfigureAwait(false);

        var dialog = Page.Locator(".mud-dialog");
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 }).ConfigureAwait(false);
        if (ConfirmDialogTitle is { } title)
        {
            await Expect(dialog).ToContainTextAsync(title).ConfigureAwait(false);
        }

        if (ConfirmDialogMessage is { } message)
        {
            await Expect(dialog).ToContainTextAsync(message).ConfigureAwait(false);
        }

        return dialog;
    }
}

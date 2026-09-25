using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;

namespace MMCA.Common.Testing.E2E.PageObjects;

/// <summary>
/// Role administration (ADR-116): the roster at <c>/roles</c> and the permission editor at
/// <c>/roles/{role}</c>.
/// </summary>
/// <remarks>
/// Every selector is a <c>data-testid</c> the framework's shared <c>RoleAdminList</c> /
/// <c>RoleAdminEdit</c> components already render, plus the <c>data-permission</c> attribute each
/// checkbox carries. Those are stable identifiers rather than MudBlazor class names, which is what
/// keeps this page object from breaking on a component-library upgrade (the checkbox input itself is
/// visually hidden behind MudBlazor's icon button, so the attribute is also the only reliable way to
/// reach one specific permission).
/// </remarks>
public sealed class RoleAdminPage
{
    private readonly IPage _page;

    public RoleAdminPage(IPage page) => _page = page;

    /// <summary>The roster table, which is what proves the list loaded rather than failed.</summary>
    public ILocator RoleTable => _page.Locator(".mud-table");

    /// <summary>Every edit link on the roster.</summary>
    public ILocator EditLinks => _page.GetByTestId("edit-role");

    /// <summary>The editor's save button.</summary>
    public ILocator SaveButton => _page.GetByTestId("save-permissions");

    public async Task GotoListAsync() =>
        await _page.GotoProtectedAsync("/roles").ConfigureAwait(false);

    public async Task GotoEditorAsync(string role) =>
        await _page.GotoProtectedAsync($"/roles/{role}").ConfigureAwait(false);

    /// <summary>The roster row for one role.</summary>
    /// <param name="role">The role name.</param>
    public ILocator RowByRole(string role) =>
        _page.Locator(".mud-table-body .mud-table-row").Filter(new() { HasText = role });

    /// <summary>
    /// One permission's checkbox in the editor. MudCheckBox splats unmatched attributes straight onto
    /// its own <c>input</c>, so the permission name is on the checkbox itself, not on a wrapper.
    /// </summary>
    /// <param name="permission">The permission string, e.g. <c>orders:refunds:issue</c>.</param>
    public ILocator PermissionCheckbox(string permission) =>
        _page.Locator($"input[data-permission='{permission}']");

    /// <summary>
    /// Sets one permission's checkbox and saves, then waits for the save to be acknowledged.
    /// </summary>
    /// <param name="permission">The permission to grant or revoke.</param>
    /// <param name="granted">Whether the role should end up holding it.</param>
    public async Task SetPermissionAsync(string permission, bool granted)
    {
        // Force: MudBlazor renders the real input at zero opacity underneath its icon button, so
        // Playwright's actionability check would time out on a control the user can click perfectly
        // well. SetCheckedAsync still drives the input, which is what the component binds to.
        await PermissionCheckbox(permission).SetCheckedAsync(granted, new() { Force = true }).ConfigureAwait(false);
        await SaveButton.ClickAsync().ConfigureAwait(false);

        // Wait for the success snackbar before returning. The save runs on the server side of the
        // Blazor circuit, so the click alone proves nothing: navigating away before the PUT lands
        // tears the circuit down, cancels the request, and the Identity service logs the write as
        // "Operation cancelled by user" (a consumer deploy run once lost the write on all three tries).
        await Assertions.Expect(_page.GetByText("Stored permissions saved."))
            .ToBeVisibleAsync(new() { Timeout = 15_000 }).ConfigureAwait(false);
    }
}

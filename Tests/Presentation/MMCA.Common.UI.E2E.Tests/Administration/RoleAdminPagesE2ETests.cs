using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using MMCA.Common.UI.E2E.Tests.Infrastructure;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MMCA.Common.UI.E2E.Tests.Administration;

/// <summary>
/// Render-smoke + WCAG 2.1 AA accessibility scans for the shared role administration components
/// (ADR-116), hosted by the gallery at <c>/admin/roles</c> and <c>/admin/roles/{role}</c> against
/// its canned <c>StubRoleAdminUIService</c>. These are the scans that hold the per-row edit link and
/// the save button to WCAG 2.5.3 Label in Name (axe <c>label-content-name-mismatch</c>).
/// </summary>
public sealed class RoleAdminPagesE2ETests : GalleryAxeTestBase
{
    public RoleAdminPagesE2ETests(PlaywrightFixture playwright, GalleryHostFixture gallery)
        : base(playwright, gallery)
    {
    }

    [Fact]
    public async Task RoleList_Renders_AndHasNoWcag21AaViolations()
    {
        await Page.GotoAndWaitForBlazorAsync("/admin/roles");

        // The per-row link is named "<visible text> for <role>", so it is found by that name.
        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = "Edit permissions for Admin" })).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("edit-role")).ToHaveCountAsync(2);

        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);
    }

    [Fact]
    public async Task RoleEditor_Renders_AndHasNoWcag21AaViolations()
    {
        await Page.GotoAndWaitForBlazorAsync("/admin/roles/Admin");

        // The save button carries no aria-label, so its visible text is its accessible name.
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Save permissions", Exact = true })).ToBeVisibleAsync();
        await Expect(Page.Locator("input[data-permission='orders:write']")).ToHaveCountAsync(1);

        await Page.AssertNoAccessibilityViolationsAsync(AxeOptions.Wcag21Aa);
    }
}

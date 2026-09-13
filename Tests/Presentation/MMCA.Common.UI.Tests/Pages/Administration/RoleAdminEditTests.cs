using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Permissions;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Pages.Administration;
using MMCA.Common.UI.Services.Administration;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Administration;

/// <summary>
/// bUnit tests for the shared <see cref="RoleAdminEdit"/> component (the editor half of ADR-116's
/// role administration): the catalog grouped into fieldsets, the two kinds of locked checkbox, the
/// full stored set a save submits, and how a refusal reaches the operator.
/// </summary>
public sealed class RoleAdminEditTests : BunitTestBase
{
    private const string Role = "Admin";
    private const string OrdersRead = "orders:read";
    private const string OrdersWrite = "orders:write";
    private const string ReportsRun = "reports:run";

    private readonly Mock<IRoleAdminUIService> _roles = new();
    private readonly Mock<IToastService> _toast = new();

    public RoleAdminEditTests()
    {
        Services.AddSingleton(_roles.Object);

        // Registered after the base class's default facade so this wins, and the component's toasts
        // can be counted without rendering a snackbar provider.
        Services.AddSingleton<IToastService>(_toast.Object);

        SetupRole(compiled: [OrdersRead], stored: [ReportsRun]);
        SetupCatalog(OrdersRead, OrdersWrite, ReportsRun, AdministrationPermissions.ManageRoles);

        _roles.Setup(x => x.SetStoredPermissionsAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new RolePermissionsResponse(Role, [OrdersRead], [ReportsRun])));
    }

    // ── Render ──
    [Fact]
    public async Task RendersOneFieldsetPerPermissionArea()
    {
        var cut = RenderEdit();

        await cut.WaitForAssertionAsync(() => cut.FindAll("fieldset").Should().HaveCount(3));
        cut.FindAll("fieldset legend").Select(legend => legend.TextContent.Trim())
            .Should().Equal("orders", "reports", "roles");
    }

    [Fact]
    public async Task RendersOneCheckboxPerCatalogPermission()
    {
        var cut = RenderEdit();

        await cut.WaitForAssertionAsync(() => Checkboxes(cut).Should().HaveCount(4));
    }

    /// <summary>
    /// A permission the host compiled in is ticked and locked: removing it is a code change, and a
    /// checkbox that silently did nothing would say otherwise.
    /// </summary>
    [Fact]
    public async Task ACompiledPermissionIsTickedAndDisabled()
    {
        var cut = RenderEdit();

        await cut.WaitForAssertionAsync(() => Checkboxes(cut).Should().HaveCount(4));

        var compiled = Checkbox(cut, OrdersRead);
        compiled.HasAttribute("disabled").Should().BeTrue();
        cut.Markup.Should().Contain("Granted in code");
    }

    /// <summary>
    /// The permission that guards this very screen is never offered: the server refuses to store it,
    /// so a tickable box would be an action guaranteed to fail.
    /// </summary>
    [Fact]
    public async Task TheManageRolesPermissionIsDisabledWithItsOwnReason()
    {
        var cut = RenderEdit();

        await cut.WaitForAssertionAsync(() => Checkboxes(cut).Should().HaveCount(4));

        Checkbox(cut, AdministrationPermissions.ManageRoles).HasAttribute("disabled").Should().BeTrue();
        cut.Markup.Should().Contain("Can only be granted in code");
    }

    [Fact]
    public async Task AnEditablePermissionIsNotDisabled()
    {
        var cut = RenderEdit();

        await cut.WaitForAssertionAsync(() => Checkboxes(cut).Should().HaveCount(4));

        Checkbox(cut, OrdersWrite).HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task WithAnEmptyCatalog_RendersTheEmptyState()
    {
        SetupCatalog();

        var cut = RenderEdit();

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("nothing to grant"));
    }

    [Fact]
    public async Task WhenTheLoadFails_RendersTheInlineFailureAndARetry()
    {
        _roles.Setup(x => x.GetAsync(Role, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<RolePermissionsResponse>(
                Error.NotFoundError("Authorization.RoleNotFound", "The role was not found.")));

        var cut = RenderEdit();

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("The role was not found."));
        cut.Markup.Should().Contain("Try again");
        cut.FindAll("fieldset").Should().BeEmpty();
    }

    // ── Save ──
    [Fact]
    public async Task Saving_SendsTheWholeStoredSetIncludingTheNewlyTickedPermission()
    {
        var cut = RenderEdit();
        await cut.WaitForAssertionAsync(() => Checkboxes(cut).Should().HaveCount(4));

        await Checkbox(cut, OrdersWrite).ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });
        await cut.Find("[data-testid=\"save-permissions\"]").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _roles.Verify(
            x => x.SetStoredPermissionsAsync(
                Role,
                It.Is<IReadOnlyList<string>>(set => set.SequenceEqual(new[] { OrdersWrite, ReportsRun })),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _toast.Verify(x => x.Success("Stored permissions saved."), Times.Once);
    }

    [Fact]
    public async Task Saving_AfterUntickingAStoredPermission_SendsTheSetWithoutIt()
    {
        var cut = RenderEdit();
        await cut.WaitForAssertionAsync(() => Checkboxes(cut).Should().HaveCount(4));

        await Checkbox(cut, ReportsRun).ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = false });
        await cut.Find("[data-testid=\"save-permissions\"]").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _roles.Verify(
            x => x.SetStoredPermissionsAsync(
                Role,
                It.Is<IReadOnlyList<string>>(set => set.Count == 0),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The server's refusal names the permission it rejected, so it is rendered inline rather than
    /// only raised as a toast that expires.
    /// </summary>
    [Fact]
    public async Task WhenTheSaveIsRefused_TheServersReasonIsRenderedInline()
    {
        _roles.Setup(x => x.SetStoredPermissionsAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<RolePermissionsResponse>(Error.Validation(
                "PermissionGrant.UnknownPermission",
                "The host's permission catalog does not contain \"orders:wrote\".")));
        var cut = RenderEdit();
        await cut.WaitForAssertionAsync(() => Checkboxes(cut).Should().HaveCount(4));

        await cut.Find("[data-testid=\"save-permissions\"]").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("does not contain"));
        _toast.Verify(x => x.Error("Failed to save the stored permissions."), Times.Once);
    }

    private void SetupRole(IReadOnlyList<string> compiled, IReadOnlyList<string> stored) =>
        _roles.Setup(x => x.GetAsync(Role, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new RolePermissionsResponse(Role, compiled, stored)));

    private void SetupCatalog(params string[] permissions) =>
        _roles.Setup(x => x.GetCatalogAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new PermissionCatalogResponse([Role], permissions)));

    private IRenderedComponent<RoleAdminEdit> RenderEdit() =>
        RenderUnderTest<RoleAdminEdit>(p =>
        {
            p.Add(x => x.Role, Role);
            p.Add(x => x.ListHref, "/admin/roles");
        });

    // MudCheckBox splats unmatched attributes straight onto its <input>, so the test hooks are on
    // the checkbox itself rather than on a wrapper.
    private static IReadOnlyList<AngleSharp.Dom.IElement> Checkboxes(IRenderedComponent<RoleAdminEdit> cut) =>
        cut.FindAll("input[data-testid=\"permission-checkbox\"]");

    /// <summary>The checkbox input for one permission.</summary>
    /// <param name="cut">The rendered component.</param>
    /// <param name="permission">The permission whose checkbox is wanted.</param>
    /// <returns>The checkbox input element.</returns>
    private static AngleSharp.Dom.IElement Checkbox(IRenderedComponent<RoleAdminEdit> cut, string permission) =>
        cut.Find($"input[data-permission=\"{permission}\"]");
}

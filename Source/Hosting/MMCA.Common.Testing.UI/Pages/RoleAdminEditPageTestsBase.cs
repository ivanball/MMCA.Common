using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Services.Administration;
using Moq;
using Xunit;

namespace MMCA.Common.Testing.UI.Pages;

/// <summary>
/// Shared facts for a consumer's role editor page: the thin routed shell over the framework's
/// <c>RoleAdminEdit</c> (ADR-116). What is pinned is the consumer's own half: the role arrives from
/// the route and reaches the component, the catalog renders as checkboxes with the compiled
/// permission locked, a save submits the STORED set, and the page links back to the consumer's
/// roster. A consumer keeps a one-line sealed subclass naming its page, its roles, one compiled and
/// one storable permission, and its roster route (the ADR-015 pattern).
/// </summary>
/// <typeparam name="TPage">The consumer's routed editor page; it takes the role as a parameter.</typeparam>
public abstract class RoleAdminEditPageTestsBase<TPage> : BunitComponentTestBase
    where TPage : IComponent
{
    protected RoleAdminEditPageTestsBase() => Services.AddSingleton(Roles.Object);

    /// <summary>Gets the mocked role-administration client the page's component calls.</summary>
    protected Mock<IRoleAdminUIService> Roles { get; } = new();

    /// <summary>Gets the role the editor is opened on.</summary>
    protected abstract string EditedRole { get; }

    /// <summary>Gets another role the catalog lists.</summary>
    protected abstract string OtherRole { get; }

    /// <summary>
    /// Gets a permission the consumer's compiled registry grants <see cref="EditedRole"/>, so it
    /// renders ticked and locked.
    /// </summary>
    protected abstract string CompiledPermission { get; }

    /// <summary>
    /// Gets a permission the compiled registry does NOT grant <see cref="EditedRole"/>, so it is
    /// editable and can be stored.
    /// </summary>
    protected abstract string StoredPermission { get; }

    /// <summary>Gets the consumer's roster route the editor links back to (for example <c>/roles</c>).</summary>
    protected abstract string RosterRoute { get; }

    /// <summary>Gets the name of the page's route parameter carrying the role. Defaults to <c>Role</c>.</summary>
    protected virtual string RoleParameterName => "Role";

    [Fact]
    public void TheRouteRole_ReachesTheComponentAndItsCatalogRenders()
    {
        RenderMudProviders();

        var cut = RenderEditor();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain(EditedRole);
            cut.FindAll("input[data-permission]").Should().HaveCount(2);
        });
    }

    /// <summary>
    /// A permission the host COMPILES in for the role is ticked and locked: removing it is a code
    /// change, not a data edit, and the server refuses to store it either way.
    /// </summary>
    [Fact]
    public void ACompiledPermission_IsLocked_AndAnEditableOneIsNot()
    {
        RenderMudProviders();

        var cut = RenderEditor();

        // MudCheckBox splats unmatched attributes straight onto its own <input>, so the test hooks are
        // on the checkbox itself rather than on a wrapper element.
        cut.WaitForAssertion(() =>
        {
            cut.Find($"input[data-permission='{CompiledPermission}']").HasAttribute("disabled").Should().BeTrue();
            cut.Find($"input[data-permission='{StoredPermission}']").HasAttribute("disabled").Should().BeFalse();
        });
    }

    /// <summary>
    /// Ticking an editable permission and saving submits it as the role's complete STORED set, which
    /// is the one write this page performs.
    /// </summary>
    [Fact]
    public void TickingAPermissionAndSaving_SubmitsItAsTheStoredSet()
    {
        RenderMudProviders();

        var cut = RenderEditor();
        cut.WaitForAssertion(() => cut.Find($"input[data-permission='{StoredPermission}']"));

        cut.Find($"input[data-permission='{StoredPermission}']").Change(true);
        cut.Find("[data-testid=save-permissions]").Click();

        cut.WaitForAssertion(() => Roles.Verify(
            x => x.SetStoredPermissionsAsync(
                EditedRole,
                It.Is<IReadOnlyList<string>>(p => p.Count == 1 && p[0] == StoredPermission),
                It.IsAny<CancellationToken>()),
            Times.Once));
    }

    /// <summary>
    /// The link back to the roster is the consumer's route, not a component default: without it the
    /// editor is a dead end on a page whose only other affordance is a save.
    /// </summary>
    [Fact]
    public void ThePage_LinksBackToTheRoleRoster()
    {
        RenderMudProviders();

        var cut = RenderEditor();

        cut.WaitForAssertion(() =>
            cut.FindAll($"a[href='{RosterRoute}']").Should().NotBeEmpty());
    }

    /// <summary>
    /// Stubs the client for <see cref="EditedRole"/> (unless a fact already did) and renders
    /// <typeparamref name="TPage"/> with that role as its route parameter.
    /// </summary>
    /// <returns>The rendered page.</returns>
    protected IRenderedComponent<TPage> RenderEditor()
    {
        SetupDefaultClient();
        return RenderUnderTest<TPage>(p => p.TryAdd(RoleParameterName, EditedRole));
    }

    // Set at render rather than in the constructor: the role and permission names are abstract
    // members, and a constructor must not call them before the derived class has initialized.
    private void SetupDefaultClient()
    {
        Roles
            .Setup(x => x.GetAsync(EditedRole, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(
                new RolePermissionsResponse(EditedRole, [CompiledPermission], [])));
        Roles
            .Setup(x => x.GetCatalogAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new PermissionCatalogResponse(
                [EditedRole, OtherRole],
                [CompiledPermission, StoredPermission])));
        Roles
            .Setup(x => x.SetStoredPermissionsAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(
                new RolePermissionsResponse(EditedRole, [CompiledPermission], [StoredPermission])));
    }
}

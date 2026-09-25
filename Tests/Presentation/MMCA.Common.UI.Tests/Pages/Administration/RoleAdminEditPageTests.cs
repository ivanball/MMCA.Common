using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using MMCA.Common.Testing.UI.Pages;
using MMCA.Common.UI.Pages.Administration;

namespace MMCA.Common.UI.Tests.Pages.Administration;

/// <summary>
/// Runs the shared <see cref="RoleAdminEditPageTestsBase{TPage}"/> facts over a consumer-shaped
/// routed shell, so the base the consumers subclass is exercised in this repo's own CI.
/// </summary>
public sealed class RoleAdminEditPageTests : RoleAdminEditPageTestsBase<RoleAdminEditPageTests.EditorShell>
{
    private const string Roster = "/admin/roles";

    protected override string EditedRole => "Auditor";

    protected override string OtherRole => "Admin";

    protected override string CompiledPermission => "reports:run";

    protected override string StoredPermission => "orders:read";

    protected override string RosterRoute => Roster;

    /// <summary>A consumer-shaped editor page: the role from the route plus the app's roster route.</summary>
    public sealed class EditorShell : ComponentBase
    {
        [Parameter]
        public string Role { get; set; } = string.Empty;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<RoleAdminEdit>(0);
            builder.AddComponentParameter(1, nameof(RoleAdminEdit.Role), Role);
            builder.AddComponentParameter(2, nameof(RoleAdminEdit.ListHref), Roster);
            builder.CloseComponent();
        }
    }
}

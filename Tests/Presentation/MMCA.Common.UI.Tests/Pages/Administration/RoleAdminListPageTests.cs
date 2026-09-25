using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using MMCA.Common.Testing.UI.Pages;
using MMCA.Common.UI.Pages.Administration;

namespace MMCA.Common.UI.Tests.Pages.Administration;

/// <summary>
/// Runs the shared <see cref="RoleAdminListPageTestsBase{TPage}"/> facts over a consumer-shaped
/// routed shell, so the base the consumers subclass is exercised in this repo's own CI.
/// </summary>
public sealed class RoleAdminListPageTests : RoleAdminListPageTestsBase<RoleAdminListPageTests.RosterShell>
{
    protected override string FirstRole => "Admin";

    protected override string SecondRole => "Auditor";

    protected override string EditRoute(string role) => RosterShell.EditRoute(role);

    /// <summary>A consumer-shaped roster page: the framework list plus the app's edit route.</summary>
    public sealed class RosterShell : ComponentBase
    {
        public static string EditRoute(string role) => "/admin/roles/" + Uri.EscapeDataString(role);

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<RoleAdminList>(0);
            builder.AddComponentParameter(1, nameof(RoleAdminList.EditHref), (Func<string, string>)EditRoute);
            builder.CloseComponent();
        }
    }
}

using AwesomeAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using MMCA.Common.Testing.UI;

namespace MMCA.Common.UI.Tests.Infrastructure;

/// <summary>
/// Pins the authorization the shipped bUnit base wires: the real authorization service, so an
/// <c>&lt;AuthorizeView Roles="..."&gt;</c> denies a principal outside the role exactly as the app
/// does, and a role-less view still separates anonymous from authenticated. Derived from the SHIPPED
/// base, because that is what every consumer's component test inherits.
/// </summary>
public sealed class BunitComponentTestBaseAuthorizationTests : BunitComponentTestBase
{
    [Fact]
    public void RoleGatedView_DeniesAnAuthenticatedPrincipalOutsideTheRole()
    {
        var view = RenderAs<AuthorizeView>(TestPrincipal.InRole("Customer"), p => p
            .Add(x => x.Roles, "Admin")
            .Add(x => x.Authorized, _ => "<span>admin</span>")
            .Add(x => x.NotAuthorized, _ => "<span>denied</span>"));

        view.Markup.Should().Contain("denied", "a Customer is not in the Admin role");
        view.Markup.Should().NotContain("admin");
    }

    [Fact]
    public void RoleGatedView_AuthorizesAPrincipalInTheRole()
    {
        var view = RenderAs<AuthorizeView>(TestPrincipal.InRole("Admin"), p => p
            .Add(x => x.Roles, "Admin")
            .Add(x => x.Authorized, _ => "<span>admin</span>")
            .Add(x => x.NotAuthorized, _ => "<span>denied</span>"));

        view.Markup.Should().Contain("admin");
        view.Markup.Should().NotContain("denied");
    }

    [Fact]
    public void RolelessView_StillDeniesTheAnonymousUser()
    {
        var view = RenderAs<AuthorizeView>(Anonymous, p => p
            .Add(x => x.Authorized, _ => "<span>signed-in</span>")
            .Add(x => x.NotAuthorized, _ => "<span>denied</span>"));

        view.Markup.Should().Contain("denied");
        view.Markup.Should().NotContain("signed-in");
    }
}

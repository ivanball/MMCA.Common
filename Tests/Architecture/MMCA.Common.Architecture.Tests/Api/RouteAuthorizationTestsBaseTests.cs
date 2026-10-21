using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Api;

/// <summary>
/// Coverage for <see cref="RouteAuthorizationTestsBase"/>'s role check (L198). <c>AuthorizeAttribute</c>
/// allows multiple instances and is inherited, and ASP.NET Core enforces every one of them, so a page
/// keeps its role gate when ANY of its authorize attributes names the role: a sibling policy-only
/// attribute or a derived page's bare <c>[Authorize]</c> must not hide it. The drifted subclass is
/// private so xUnit does not collect its inherited facts as tests of their own.
/// </summary>
public sealed class RouteAuthorizationTestsBaseTests
{
    private const string AdminRole = "Admin";

    [Theory]
    [InlineData(typeof(RoleGatedPage), true)]
    [InlineData(typeof(PolicyThenRolePage), true)]
    [InlineData(typeof(RoleThenPolicyPage), true)]
    [InlineData(typeof(BareOverRoleGatedPage), true)]
    [InlineData(typeof(BareOnlyPage), false)]
    [InlineData(typeof(OtherRolePlusBarePage), false)]
    [InlineData(typeof(AnonymousPage), false)]
    public void RequiresRole_ReadsEveryAuthorizeAttribute(Type page, bool expected) =>
        RoleProbe.Requires(page, AdminRole).Should().Be(expected);

    [Fact]
    public void GovernedPages_WithStackedOrInheritedAttributes_KeepTheirRoleGate()
    {
        var assert = new StackedAttributeTests().GovernedPages_RequireDeclaredRole;

        assert.Should().NotThrow("each governed page carries [Authorize(Roles = \"Admin\")] among its authorize attributes");
    }

    [Fact]
    public void GovernedPages_WithoutTheRole_StillFail()
    {
        var assert = new MissingRoleTests().GovernedPages_RequireDeclaredRole;

        assert.Should().Throw<Exception>()
            .Which.Message.Should().Contain(nameof(OtherRolePlusBarePage));
    }

    [Route("/fixture/role-gated")]
    [Authorize(Roles = AdminRole)]
    public class RoleGatedPage;

    [Route("/fixture/policy-then-role")]
    [Authorize(Policy = "Fixture")]
    [Authorize(Roles = AdminRole)]
    public sealed class PolicyThenRolePage;

    [Route("/fixture/role-then-policy")]
    [Authorize(Roles = AdminRole)]
    [Authorize(Policy = "Fixture")]
    public sealed class RoleThenPolicyPage;

    [Route("/fixture/bare-over-role-gated")]
    [Authorize]
    public sealed class BareOverRoleGatedPage : RoleGatedPage;

    [Route("/fixture/bare-only")]
    [Authorize]
    public sealed class BareOnlyPage;

    [Route("/fixture/other-role")]
    [Authorize(Roles = "Other")]
    [Authorize]
    public sealed class OtherRolePlusBarePage;

    [Route("/fixture/anonymous")]
    public sealed class AnonymousPage;

    private sealed class RoleProbe : RouteAuthorizationTestsBase
    {
        protected override Assembly TargetAssembly => typeof(RoleProbe).Assembly;

        protected override string RequiredRole => AdminRole;

        public static bool Requires(Type page, string role) => RequiresRole(page, role);

        protected override bool IsGovernedPage(Type pageType) => false;
    }

    private sealed class StackedAttributeTests : RouteAuthorizationTestsBase
    {
        protected override Assembly TargetAssembly => typeof(StackedAttributeTests).Assembly;

        protected override string RequiredRole => AdminRole;

        protected override bool IsGovernedPage(Type pageType) =>
            pageType == typeof(PolicyThenRolePage)
            || pageType == typeof(RoleThenPolicyPage)
            || pageType == typeof(BareOverRoleGatedPage);
    }

    private sealed class MissingRoleTests : RouteAuthorizationTestsBase
    {
        protected override Assembly TargetAssembly => typeof(MissingRoleTests).Assembly;

        protected override string RequiredRole => AdminRole;

        protected override bool IsGovernedPage(Type pageType) => pageType == typeof(OtherRolePlusBarePage);
    }
}

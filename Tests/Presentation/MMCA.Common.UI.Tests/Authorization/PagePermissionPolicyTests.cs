using System.Reflection;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Auth;
using MMCA.Common.UI.Authorization;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Interfaces;

namespace MMCA.Common.UI.Tests.Authorization;

/// <summary>
/// The page permission guard: <see cref="HasPagePermissionAttribute"/> on a page in a registered UI
/// module's assembly yields a policy that <c>AddUIShared</c> registers with no host call, and the
/// policy passes exactly the signed-in callers whose token carries that permission claim (the same
/// check <see cref="NavItem.RequiredPermission"/> makes, so a menu entry and its page agree).
/// </summary>
public sealed class PagePermissionPolicyTests
{
    private const string Permission = "widgets:manage";

    [Fact]
    public async Task ThePolicyForAModulePagePermission_IsRegistered_AndAnUnusedPermissionHasNone()
    {
        await using var provider = BuildProvider();
        var policies = provider.GetRequiredService<IAuthorizationPolicyProvider>();

        (await policies.GetPolicyAsync(PagePermissionPolicy.NameFor(Permission))).Should().NotBeNull();
        (await policies.GetPolicyAsync(PagePermissionPolicy.NameFor("unused:manage"))).Should().BeNull(
            "only permissions a page actually declares become policies");
    }

    [Fact]
    public async Task ASignedInUserWithThePermissionClaim_Passes()
    {
        var result = await AuthorizeAsync(SignedIn(new Claim(AuthClaimTypes.Permission, Permission)));

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task ASignedInUserWithoutThePermissionClaim_Fails()
    {
        var result = await AuthorizeAsync(SignedIn(new Claim(AuthClaimTypes.Permission, "other:manage")));

        result.Succeeded.Should().BeFalse("a different capability is not the one the page asked for");
    }

    [Fact]
    public async Task AnAnonymousCaller_Fails_EvenCarryingThePermissionClaim()
    {
        var unauthenticated = new ClaimsPrincipal(new ClaimsIdentity([new Claim(AuthClaimTypes.Permission, Permission)]));

        var result = await AuthorizeAsync(unauthenticated);

        result.Succeeded.Should().BeFalse("the page needs a signed-in caller first");
    }

    private static ClaimsPrincipal SignedIn(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Test"));

    private static async Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user)
    {
        await using var provider = BuildProvider();
        return await provider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(user, PagePermissionPolicy.NameFor(Permission));
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddUIShared(new ConfigurationBuilder().Build());
        services.AddSingleton<IUIModule, WidgetsModule>();
        return services.BuildServiceProvider();
    }

    private sealed class WidgetsModule : IUIModule
    {
        public IReadOnlyList<NavItem> NavItems { get; } = [];

        public Assembly Assembly => typeof(WidgetsModule).Assembly;
    }

    [Route("/widgets")]
    [HasPagePermission(Permission)]
    private sealed class WidgetsPage : ComponentBase;
}

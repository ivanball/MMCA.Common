using System.Reflection;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Authorization;
using MMCA.Common.Shared.Auth;
using MMCA.Common.UI.Authorization;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Interfaces;

namespace MMCA.Common.UI.Web.Tests.Security;

/// <summary>
/// A web host that wires both the shared UI (<c>AddUIShared</c>) and the API authorization
/// (<c>AddAuthorizationPolicies</c>, which REPLACES the policy provider with one that intercepts every
/// <c>perm:</c> name) must still resolve the page permission policies, because their
/// <c>page-perm:</c> prefix falls through to the default provider that reads the registered options.
/// </summary>
public sealed class PagePermissionPolicyCoexistenceTests
{
    private const string Permission = "widgets:manage";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ThePagePolicyResolves_WhicheverOrderTheHostRegistersUIAndAPIAuthorization(bool apiFirst)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (apiFirst)
        {
            services.AddAuthorizationPolicies();
        }

        services.AddUIShared(new ConfigurationBuilder().Build());
        if (!apiFirst)
        {
            services.AddAuthorizationPolicies();
        }

        services.AddSingleton<IUIModule, WidgetsModule>();
        await using var provider = services.BuildServiceProvider();

        var granted = new ClaimsPrincipal(new ClaimsIdentity([new Claim(AuthClaimTypes.Permission, Permission)], "Test"));
        var denied = new ClaimsPrincipal(new ClaimsIdentity([], "Test"));
        var authorization = provider.GetRequiredService<IAuthorizationService>();

        (await authorization.AuthorizeAsync(granted, PagePermissionPolicy.NameFor(Permission))).Succeeded.Should().BeTrue();
        (await authorization.AuthorizeAsync(denied, PagePermissionPolicy.NameFor(Permission))).Succeeded.Should().BeFalse();
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

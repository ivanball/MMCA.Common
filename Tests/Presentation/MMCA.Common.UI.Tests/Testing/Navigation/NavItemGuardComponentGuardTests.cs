using System.Reflection;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Testing.UI.Navigation;
using MMCA.Common.UI.Common;

namespace MMCA.Common.UI.Tests.Testing.Navigation;

/// <summary>
/// The guard gate's reading of a framework component's guard (its [Authorize] attributes and the
/// requirements of the policies they name) and its claim comparison, driven directly because the
/// framework's own pages exercise only the policy shapes MMCA.Common.UI registers today. Each policy
/// requirement kind, an unregistered policy and an unknown requirement are proven here, so a policy a
/// future framework page adds is read the way the gate documents.
/// </summary>
public sealed class NavItemGuardComponentGuardTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    [Fact]
    public void AnAllowAnonymousComponent_IsAnAnonymousGuard()
    {
        var guard = ReadGuard(typeof(AnonymousComponent), new AuthorizationOptions());

        guard.Problem.Should().BeNull();
        guard.RequiresAuthentication.Should().BeFalse();
    }

    [Fact]
    public void AComponentWithoutAuthorize_IsAnAnonymousGuard()
    {
        var guard = ReadGuard(typeof(UnguardedComponent), new AuthorizationOptions());

        guard.Problem.Should().BeNull();
        guard.RequiresAuthentication.Should().BeFalse();
    }

    [Fact]
    public void RolesFromTheAttributeAndFromThePolicy_AreBothRead()
    {
        var guard = ReadGuard(typeof(RolesComponent), Policies());

        guard.Problem.Should().BeNull();
        guard.RequiresAuthentication.Should().BeTrue();
        guard.Roles.Should().Equal("Admin", "Member", "Owner");
        guard.ClaimType.Should().BeNull();
        guard.Permission.Should().BeNull();
    }

    [Fact]
    public void APermissionClaimWithOneValue_IsThePermission()
    {
        var guard = ReadGuard(typeof(PermissionClaimComponent), Policies());

        guard.Problem.Should().BeNull();
        guard.Permission.Should().Be("reports:view");
        guard.ClaimType.Should().BeNull();
    }

    [Fact]
    public void APermissionClaimWithSeveralValues_IsAClaimType()
    {
        var guard = ReadGuard(typeof(PermissionClaimAnyOfComponent), Policies());

        guard.Problem.Should().BeNull();
        guard.ClaimType.Should().Be(AuthClaimTypes.Permission);
        guard.Permission.Should().BeNull();
    }

    [Fact]
    public void AnyOtherClaim_IsAClaimType()
    {
        var guard = ReadGuard(typeof(ClaimComponent), Policies());

        guard.Problem.Should().BeNull();
        guard.ClaimType.Should().Be("tenant");
    }

    [Fact]
    public void APagePermissionRequirement_IsThePermission_AndANotificationPermissionDoesNotOverrideIt()
    {
        var guard = ReadGuard(typeof(PagePermissionComponent), Policies());

        guard.Problem.Should().BeNull();
        guard.RequiresAuthentication.Should().BeTrue();
        guard.Permission.Should().Be("pages:view", "a PagePermissionRequirement sets the permission and a NotificationPageRequirement only fills one that is unset");
    }

    [Fact]
    public void ANotificationPageRequirement_IsItsPermission()
    {
        var guard = ReadGuard(typeof(NotificationComponent), Policies());

        guard.Problem.Should().BeNull();
        guard.Permission.Should().Be("notifications:read");
    }

    [Fact]
    public void AnUnregisteredPolicy_IsAProblem()
    {
        var guard = ReadGuard(typeof(MissingPolicyComponent), Policies());

        guard.Problem.Should().Be("policy 'Missing' is not registered by AddUIShared");
    }

    [Fact]
    public void AnUnknownRequirement_IsAProblemNamingIt()
    {
        var guard = ReadGuard(typeof(UnknownRequirementComponent), Policies());

        guard.Problem.Should().StartWith("policy 'Unknown' carries requirement UnknownRequirement, which this gate does not know how to compare");
    }

    [Fact]
    public void ADescription_ListsEveryAuthorizeAttribute()
    {
        var guard = ReadGuard(typeof(ClaimComponent), Policies());

        guard.Description.Should().Be("[Authorize(Roles = -, Policy = Claim)]");
    }

    [Fact]
    public void AClaimGuard_NeedsAnItemRequiringThatClaim()
    {
        var missing = CompareClaim(new NavItem("Tenants", "/tenants", string.Empty, typeof(NavItemGuardComponentGuardTests)), "tenant");
        var other = CompareClaim(new NavItem("Tenants", "/tenants", string.Empty, typeof(NavItemGuardComponentGuardTests), RequiredClaim: "region"), "tenant");
        var matching = CompareClaim(new NavItem("Tenants", "/tenants", string.Empty, typeof(NavItemGuardComponentGuardTests), RequiredClaim: "tenant"), "tenant");

        missing.Should().Be("the page requires the claim 'tenant' but the item requires claim '<none>'");
        other.Should().Be("the page requires the claim 'tenant' but the item requires claim 'region'");
        matching.Should().BeNull();
    }

    private static AuthorizationOptions Policies()
    {
        var options = new AuthorizationOptions();
        options.AddPolicy("Owners", policy => policy.RequireAuthenticatedUser().RequireRole("Owner"));
        options.AddPolicy("PermissionClaim", policy => policy.RequireClaim(AuthClaimTypes.Permission, "reports:view"));
        options.AddPolicy("PermissionClaimAnyOf", policy => policy.RequireClaim(AuthClaimTypes.Permission, "reports:view", "reports:edit"));
        options.AddPolicy("Claim", policy => policy.RequireClaim("tenant"));
        options.AddPolicy("PagePermission", policy => policy.AddRequirements(new PagePermissionRequirement("pages:view")));
        options.AddPolicy("Notification", policy => policy.AddRequirements(new NotificationPageRequirement("notifications:read")));
        options.AddPolicy("Unknown", policy => policy.AddRequirements(new UnknownRequirement()));
        return options;
    }

    private static GuardReading ReadGuard(Type component, AuthorizationOptions policies)
    {
        var method = typeof(NavItemGuardContractTestsBase).GetMethod("ReadComponentGuard", PrivateStatic)!;
        var result = (ITuple)method.Invoke(null, [component, policies])!;
        var problem = (string?)result[1];
        if (result[0] is not { } guard)
        {
            return new GuardReading(null, [], null, null, null, problem);
        }

        T Read<T>(string name) => (T)guard.GetType().GetProperty(name)!.GetValue(guard)!;
        return new GuardReading(
            Read<bool>("RequiresAuthentication"),
            Read<IReadOnlyList<string>>("AnyOfRoles"),
            (string?)guard.GetType().GetProperty("ClaimType")!.GetValue(guard),
            (string?)guard.GetType().GetProperty("Permission")!.GetValue(guard),
            Read<string>("Description"),
            problem);
    }

    private static string? CompareClaim(NavItem item, string claimType)
    {
        var guardType = typeof(NavItemGuardContractTestsBase).GetNestedType("PageGuard", BindingFlags.NonPublic)!;
        var guard = Activator.CreateInstance(guardType, [true, Array.Empty<string>(), claimType, null, "[Authorize(Policy = Claim)]"]);
        var method = typeof(NavItemGuardContractTestsBase).GetMethod("CompareClaim", PrivateStatic)!;
        return (string?)method.Invoke(null, [item, guard]);
    }

    private sealed record GuardReading(bool? RequiresAuthentication, IReadOnlyList<string> Roles, string? ClaimType, string? Permission, string? Description, string? Problem);

    [AllowAnonymous]
    private sealed class AnonymousComponent;

    private sealed class UnguardedComponent;

    [Authorize(Roles = "Admin, Member")]
    [Authorize(Policy = "Owners")]
    private sealed class RolesComponent;

    [Authorize(Policy = "PermissionClaim")]
    private sealed class PermissionClaimComponent;

    [Authorize(Policy = "PermissionClaimAnyOf")]
    private sealed class PermissionClaimAnyOfComponent;

    [Authorize(Policy = "Claim")]
    private sealed class ClaimComponent;

    [Authorize(Policy = "PagePermission")]
    [Authorize(Policy = "Notification")]
    private sealed class PagePermissionComponent;

    [Authorize(Policy = "Notification")]
    private sealed class NotificationComponent;

    [Authorize(Policy = "Missing")]
    private sealed class MissingPolicyComponent;

    [Authorize(Policy = "Unknown")]
    private sealed class UnknownRequirementComponent;

    // Named like the framework's own requirements, which the gate matches by type name.
    private sealed class PagePermissionRequirement(string permission) : IAuthorizationRequirement
    {
        public string Permission { get; } = permission;
    }

    private sealed class NotificationPageRequirement(string? permission) : IAuthorizationRequirement
    {
        public string? Permission { get; } = permission;
    }

    private sealed class UnknownRequirement : IAuthorizationRequirement;
}

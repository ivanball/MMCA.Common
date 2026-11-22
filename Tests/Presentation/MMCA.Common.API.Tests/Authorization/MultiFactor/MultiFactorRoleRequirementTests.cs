using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.Authorization.MultiFactor;
using MMCA.Common.Shared.Auth;
using static MMCA.Common.API.Tests.Authorization.MultiFactor.MultiFactorTestPrincipals;

namespace MMCA.Common.API.Tests.Authorization.MultiFactor;

/// <summary>
/// The role-level second-factor rule (<c>Authorization:MultiFactor:RequiredForRoles</c>) evaluated by
/// the real authorization service the framework registers: a permission policy is refused only for an
/// authenticated person who holds the permission, holds a listed role and carries no <c>mfa</c> claim.
/// Every other caller gets exactly the answer the permission check alone gives.
/// </summary>
public sealed class MultiFactorRoleRequirementTests
{
    private static readonly string Policy = PermissionPolicy.NameFor(Permission);

    // -- The refusal --
    [Fact]
    public async Task ListedRole_HoldingThePermission_WithoutASecondFactor_IsRefusedWithTheMultiFactorReason()
    {
        var result = await AuthorizeAsync(Authenticated(Role(Admin)), Admin);

        result.Succeeded.Should().BeFalse();
        var reason = result.Failure!.FailureReasons.Should().ContainSingle().Subject;
        reason.Message.Should().Be(AuthErrorCodes.MultiFactorRequired);
        reason.Handler.Should().BeOfType<MultiFactorRoleAuthorizationHandler>();
    }

    [Theory]
    [InlineData(AuthClaimTypes.MultiFactorMethodTotp)]
    [InlineData(AuthClaimTypes.MultiFactorMethodRecoveryCode)]
    [InlineData(AuthClaimTypes.MultiFactorMethodPasskey)]
    public async Task ListedRole_WithAnyMultiFactorMethod_IsGranted(string method)
    {
        var result = await AuthorizeAsync(Authenticated(Role(Admin), SecondFactor(method)), Admin);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("admin", ClaimTypes.Role)]
    [InlineData("ADMIN", "role")]
    [InlineData("Admin", "roles")]
    public async Task ListedRole_IsMatchedCaseInsensitively_UnderEveryRoleClaimSpelling(string role, string claimType)
    {
        // The registry grant is case-insensitive too, so the caller holds the permission either way.
        var result = await AuthorizeAsync(Authenticated(Role(role, claimType)), Admin);

        result.Succeeded.Should().BeFalse();
        result.Failure!.FailureReasons.Should().ContainSingle(r => r.Message == AuthErrorCodes.MultiFactorRequired);
    }

    [Fact]
    public async Task ListedRole_GrantedThePermissionByAClaimRatherThanTheRegistry_IsRefused()
    {
        var result = await AuthorizeAsync(Authenticated(Role(Auditor), PermissionClaim()), Auditor);

        result.Succeeded.Should().BeFalse();
        result.Failure!.FailureReasons.Should().ContainSingle(r => r.Message == AuthErrorCodes.MultiFactorRequired);
    }

    // -- Everyone else is untouched --
    [Fact]
    public async Task EmptyList_HolderWithoutASecondFactor_IsGranted()
    {
        var result = await AuthorizeAsync(Authenticated(Role(Admin)));

        result.Succeeded.Should().BeTrue("a host that sets no list sees the permission check it always had");
    }

    [Fact]
    public async Task ListOfBlankEntries_HolderWithoutASecondFactor_IsGranted()
    {
        var result = await AuthorizeAsync(Authenticated(Role(Admin)), string.Empty, "  ");

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task RoleNotListed_HolderWithoutASecondFactor_IsGranted()
    {
        var result = await AuthorizeAsync(Authenticated(Role(Member)), Admin);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task ListedRole_LackingThePermission_KeepsThePlainRefusal_WithoutTheMultiFactorReason()
    {
        var result = await AuthorizeAsync(Authenticated(Role(Auditor)), Auditor);

        result.Succeeded.Should().BeFalse();
        result.Failure!.FailureReasons.Should().NotContain(
            r => r.Message == AuthErrorCodes.MultiFactorRequired,
            "a caller without the capability is never told that a step-up exists");
    }

    [Fact]
    public async Task MachineClient_InAListedRole_IsGranted()
    {
        var result = await AuthorizeAsync(Authenticated(Role(Admin), Machine()), Admin);

        result.Succeeded.Should().BeTrue("a machine client cannot present a second factor and is exempt");
    }

    [Fact]
    public async Task ServiceToken_HoldingThePermissionByClaim_AndNoRole_IsGranted()
    {
        var service = Authenticated(new Claim("sub", "orders-service"), new Claim("svc", "orders-service"), PermissionClaim());

        var result = await AuthorizeAsync(service, Admin);

        result.Succeeded.Should().BeTrue("a service token carries no role, so no listed role can require a factor of it");
    }

    [Fact]
    public async Task UnauthenticatedPrincipal_InAListedRole_GetsNoMultiFactorReason()
    {
        var result = await AuthorizeAsync(Anonymous(Role(Admin)), Admin);

        result.Succeeded.Should().BeFalse();
        result.Failure!.FailureReasons.Should().NotContain(r => r.Message == AuthErrorCodes.MultiFactorRequired);
    }

    [Fact]
    public async Task PlainAuthenticatedPolicy_ListedRoleWithoutASecondFactor_IsGranted()
    {
        await using var provider = BuildServices(Admin);
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var plain = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

        var result = await authorization.AuthorizeAsync(Authenticated(Role(Admin)), resource: null, plain);

        result.Succeeded.Should().BeTrue("only permission requirements are subject to the role rule, so the enrollment endpoints stay reachable");
    }

    // -- Registration --
    [Fact]
    public void AddAuthorizationPolicies_CalledTwice_RegistersTheRoleHandlerOnce()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationPolicies();
        services.AddAuthorizationPolicies();

        using var provider = services.BuildServiceProvider();

        provider.GetServices<IAuthorizationHandler>().Should().ContainSingle(h => h is MultiFactorRoleAuthorizationHandler);
    }

    [Fact]
    public void Settings_WithNoConfigurationInTheContainer_ResolveEmptyWithoutThrowing()
    {
        var services = new ServiceCollection();
        services.AddAuthorizationPolicies();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<MultiFactorAuthorizationSettings>>().Value.RequiredForRoles.Should().BeEmpty();
    }

    [Fact]
    public void Settings_BindTheConfiguredRoles_OnceEvenWhenRegisteredTwice()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Authorization:MultiFactor:RequiredForRoles:0"] = Admin,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAuthorizationPolicies();
        services.AddAuthorizationPolicies();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<MultiFactorAuthorizationSettings>>().Value.RequiredForRoles
            .Should().Equal(Admin);
    }

    [Fact]
    public async Task Settings_BoundFromConfiguration_DriveTheRefusal()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [MultiFactorAuthorizationSettings.SectionName + ":RequiredForRoles:0"] = Admin,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAuthorizationPolicies();
        services.AddPermissions(grants => grants.Grant(Admin, Permission));
        await using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(Authenticated(Role(Admin)), Policy);

        result.Failure!.FailureReasons.Should().ContainSingle(r => r.Message == AuthErrorCodes.MultiFactorRequired);
    }

    private static async Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, params string[] requiredForRoles)
    {
        await using var provider = BuildServices(requiredForRoles);
        return await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, Policy);
    }

    private static ServiceProvider BuildServices(params string[] requiredForRoles)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationPolicies();
        services.AddPermissions(grants => grants
            .Grant(Admin, Permission)
            .Grant(Member, Permission));
        services.Configure<MultiFactorAuthorizationSettings>(settings =>
        {
            foreach (var role in requiredForRoles)
            {
                settings.RequiredForRoles.Add(role);
            }
        });

        return services.BuildServiceProvider();
    }
}

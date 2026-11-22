using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.Authorization.MultiFactor;
using MMCA.Common.Shared.Auth;
using static MMCA.Common.API.Tests.Authorization.MultiFactor.MultiFactorTestPrincipals;

namespace MMCA.Common.API.Tests.Authorization.MultiFactor;

/// <summary>
/// The permission check does not depend on the bearer withholding alone: a principal that still
/// carries a listed role with no second factor and no withheld marker (as if a bearer path had
/// bypassed the token-validation hook) is refused with the coded second-factor reason, while the same
/// role with the factor, or on a machine client, is granted.
/// </summary>
public sealed class MultiFactorPermissionBackstopTests
{
    private static readonly string Policy = PermissionPolicy.NameFor(Permission);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListedRoleNotWithheld_WithoutTheFactor_IsRefusedWithTheCodedReason(bool withPermissionClaim)
    {
        Claim[] claims = withPermissionClaim ? [Role(Admin), PermissionClaim()] : [Role(Admin)];
        var user = Authenticated(claims);

        var result = await AuthorizeAsync(user);

        user.HasMultiFactorWithheldRole().Should().BeFalse("the principal was built directly, not through the bearer hook");
        result.Succeeded.Should().BeFalse();
        result.Failure!.FailureReasons.Should().ContainSingle(reason => reason.Message == AuthErrorCodes.MultiFactorRequired)
            .Which.Handler.Should().BeOfType<MultiFactorRoleAuthorizationHandler>();
    }

    [Fact]
    public async Task ListedRoleNotWithheld_WithTheFactor_IsGranted()
    {
        var result = await AuthorizeAsync(Authenticated(Role(Admin), SecondFactor()));

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task ListedRoleNotWithheld_OnAMachineClient_IsGranted()
    {
        var result = await AuthorizeAsync(Authenticated(Role(Admin), Machine()));

        result.Succeeded.Should().BeTrue();
    }

    private static async Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationPolicies();
        services.AddPermissions(grants => grants.Grant(Admin, Permission));
        services.Configure<MultiFactorAuthorizationSettings>(settings => settings.RequiredForRoles.Add(Admin));
        await using var provider = services.BuildServiceProvider();

        return await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, Policy);
    }
}

using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Authorization;

namespace MMCA.Common.API.Tests.Authorization;

public sealed class PermissionPolicyProviderTests
{
    [Fact]
    public async Task GetPolicyAsync_ForPermissionPolicyName_BuildsPermissionRequirement()
    {
        var provider = CreateProvider();

        AuthorizationPolicy? policy = await provider.GetPolicyAsync("perm:sessions:manage");

        policy.Should().NotBeNull();
        policy!.Requirements.OfType<PermissionRequirement>()
            .Should().ContainSingle()
            .Which.Permission.Should().Be("sessions:manage");
    }

    [Fact]
    public async Task GetPolicyAsync_ForNonPermissionName_DelegatesToFallback()
    {
        var options = new AuthorizationOptions();
        options.AddPolicy("CustomNamed", policy => policy.RequireAuthenticatedUser());
        var provider = new PermissionPolicyProvider(Options.Create(options));

        AuthorizationPolicy? policy = await provider.GetPolicyAsync("CustomNamed");

        policy.Should().NotBeNull();
        policy!.Requirements.Should().NotContain(requirement => requirement is PermissionRequirement);
    }

    [Fact]
    public async Task GetPolicyAsync_ForUnknownNonPermissionName_ReturnsNull()
    {
        var provider = CreateProvider();

        AuthorizationPolicy? policy = await provider.GetPolicyAsync("DoesNotExist");

        policy.Should().BeNull();
    }

    private static PermissionPolicyProvider CreateProvider() =>
        new(Options.Create(new AuthorizationOptions()));
}

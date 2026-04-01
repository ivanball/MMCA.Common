using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Authorization;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.API.Tests.Authorization;

public sealed class AuthorizationExtensionsTests
{
    // ── Registration ──
    [Fact]
    public void AddAuthorizationPolicies_RegistersAuthorizationServices()
    {
        var services = new ServiceCollection();

        services.AddAuthorizationPolicies();

        services.Any(s => s.ServiceType.Equals(typeof(IAuthorizationService)))
            .Should().BeTrue();
    }

    [Fact]
    public void AddAuthorizationPolicies_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();

        IServiceCollection result = services.AddAuthorizationPolicies();

        result.Should().BeSameAs(services);
    }

    // ── RequireOrganizer policy ──
    [Fact]
    public void AddAuthorizationPolicies_RegistersRequireOrganizerPolicy()
    {
        AuthorizationPolicy policy = ResolvePolicy(AuthorizationPolicies.RequireOrganizer);

        policy.Should().NotBeNull();
        policy.Requirements.Should().ContainSingle()
            .Which.Should().BeOfType<RolesAuthorizationRequirement>()
            .Which.AllowedRoles.Should().ContainSingle()
            .Which.Should().Be(RoleNames.Organizer);
    }

    // ── RequireAttendee policy ──
    [Fact]
    public void AddAuthorizationPolicies_RegistersRequireAttendeePolicy()
    {
        AuthorizationPolicy policy = ResolvePolicy(AuthorizationPolicies.RequireAttendee);

        policy.Should().NotBeNull();
        policy.Requirements.Should().ContainSingle()
            .Which.Should().BeOfType<RolesAuthorizationRequirement>()
            .Which.AllowedRoles.Should().ContainSingle()
            .Which.Should().Be(RoleNames.Attendee);
    }

    // ── RequireAdmin policy ──
    [Fact]
    public void AddAuthorizationPolicies_RegistersRequireAdminPolicy()
    {
        AuthorizationPolicy policy = ResolvePolicy(AuthorizationPolicies.RequireAdmin);

        policy.Should().NotBeNull();
        policy.Requirements.Should().ContainSingle()
            .Which.Should().BeOfType<RolesAuthorizationRequirement>()
            .Which.AllowedRoles.Should().ContainSingle()
            .Which.Should().Be(RoleNames.Admin);
    }

    // ── RequireAuthenticated policy ──
    [Fact]
    public void AddAuthorizationPolicies_RegistersRequireAuthenticatedPolicy()
    {
        AuthorizationPolicy policy = ResolvePolicy(AuthorizationPolicies.RequireAuthenticated);

        policy.Should().NotBeNull();
        policy.Requirements.Should().ContainSingle()
            .Which.Should().BeOfType<DenyAnonymousAuthorizationRequirement>();
    }

    // ── All four policies present ──
    [Fact]
    public void AddAuthorizationPolicies_RegistersExactlyFourPolicies()
    {
        var services = new ServiceCollection();
        services.AddAuthorizationPolicies();
        ServiceProvider provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;

        string[] expectedPolicies =
        [
            AuthorizationPolicies.RequireOrganizer,
            AuthorizationPolicies.RequireAttendee,
            AuthorizationPolicies.RequireAdmin,
            AuthorizationPolicies.RequireAuthenticated,
        ];

        foreach (string policyName in expectedPolicies)
        {
            options.GetPolicy(policyName).Should().NotBeNull($"policy '{policyName}' should be registered");
        }
    }

    // ── Idempotent: calling twice does not throw ──
    [Fact]
    public void AddAuthorizationPolicies_CalledTwice_DoesNotThrow()
    {
        var services = new ServiceCollection();

        Action act = () =>
        {
            services.AddAuthorizationPolicies();
            services.AddAuthorizationPolicies();
        };

        act.Should().NotThrow();
    }

    private static AuthorizationPolicy ResolvePolicy(string policyName)
    {
        var services = new ServiceCollection();
        services.AddAuthorizationPolicies();
        ServiceProvider provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        return options.GetPolicy(policyName)!;
    }
}

using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
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

    // ── Permissions are the one authorization model ──
    [Fact]
    public void AddAuthorizationPolicies_RegistersThePermissionMechanism()
    {
        var services = new ServiceCollection();

        services.AddAuthorizationPolicies();

        services.Any(s => s.ImplementationType == typeof(PermissionAuthorizationHandler))
            .Should().BeTrue("the handler evaluates a permission policy against the registry");
        services.Any(s => s.ServiceType == typeof(IAuthorizationPolicyProvider)
                && s.ImplementationType == typeof(PermissionPolicyProvider))
            .Should().BeTrue("permission policies are materialized on demand rather than pre-registered");
        services.Any(s => s.ServiceType == typeof(IPermissionRegistry))
            .Should().BeTrue("a host that declares no grant still resolves a registry");
    }

    [Fact]
    public void AddAuthorizationPolicies_RegistersNoNamedPolicies()
    {
        var services = new ServiceCollection();
        services.AddAuthorizationPolicies();
        ServiceProvider provider = services.BuildServiceProvider();
        var policyProvider = provider.GetRequiredService<IAuthorizationPolicyProvider>();

        policyProvider.Should().BeOfType<PermissionPolicyProvider>(
            "an endpoint states the capability it needs; no role policy is pre-registered for it");
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
}

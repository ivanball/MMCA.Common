using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FeatureManagement;
using MMCA.Common.API.FeatureManagement;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Infrastructure.Context;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.API.Tests.FeatureManagement;

/// <summary>
/// End-to-end evaluation of a tenant-targeted flag through the real feature manager and the
/// standard <c>Targeting</c> filter, wired the way <c>AddAPI</c> wires it
/// (<c>WithTargeting&lt;CurrentUserTargetingContextAccessor&gt;</c>). The audience names the group
/// <c>tenant:acme</c> and nothing else, so the flag must be on for a request resolved to tenant
/// acme and off for any other tenant and for a request with no tenant (ADR-031, ADR-073).
/// </summary>
public sealed class TenantTargetedFeatureFlagTests : IDisposable
{
    private const string Flag = "Tenant.Beta";

    private readonly HttpContextAccessor _httpContextAccessor = new();
    private readonly ServiceProvider _provider;

    public TenantTargetedFeatureFlagTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"FeatureManagement:{Flag}:EnabledFor:0:Name"] = "Targeting",
                [$"FeatureManagement:{Flag}:EnabledFor:0:Parameters:Audience:Groups:0:Name"] = "tenant:acme",
                [$"FeatureManagement:{Flag}:EnabledFor:0:Parameters:Audience:Groups:0:RolloutPercentage"] = "100",
                [$"FeatureManagement:{Flag}:EnabledFor:0:Parameters:Audience:DefaultRolloutPercentage"] = "0",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHttpContextAccessor>(_httpContextAccessor);
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddFeatureManagement(configuration.GetSection("FeatureManagement"))
            .WithTargeting<CurrentUserTargetingContextAccessor>();
        _provider = services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("acme", true)]
    [InlineData("beta", false)]
    [InlineData(null, false)]
    public async Task FlagTargetingTenantAcme_IsOnOnlyForRequestsInTenantAcme(string? tenantId, bool expected)
    {
        var enabled = await EvaluateAsync(tenantId, AuthenticatedUser());

        enabled.Should().Be(expected);
    }

    // The tenant group does not depend on a signed-in caller: a tenant resolved for an anonymous
    // request (header resolution) still targets that tenant.
    [Theory]
    [InlineData("acme", true)]
    [InlineData("beta", false)]
    [InlineData(null, false)]
    public async Task FlagTargetingTenantAcme_ForAnonymousCaller_FollowsTheTenantToo(string? tenantId, bool expected)
    {
        var enabled = await EvaluateAsync(tenantId, new ClaimsPrincipal(new ClaimsIdentity()));

        enabled.Should().Be(expected);
    }

    public void Dispose() => _provider.Dispose();

    private static ClaimsPrincipal AuthenticatedUser() =>
        new(new ClaimsIdentity(
            [new Claim(AuthClaimTypes.Subject, "42"), new Claim(ClaimTypes.Role, "Member")],
            authenticationType: "TestAuth"));

    private async Task<bool> EvaluateAsync(string? tenantId, ClaimsPrincipal user)
    {
        await using var scope = _provider.CreateAsyncScope();
        if (tenantId is not null)
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        }

        _httpContextAccessor.HttpContext = new DefaultHttpContext
        {
            User = user,
            RequestServices = scope.ServiceProvider,
        };

        try
        {
            var featureManager = scope.ServiceProvider.GetRequiredService<IFeatureManager>();
            return await featureManager.IsEnabledAsync(Flag);
        }
        finally
        {
            _httpContextAccessor.HttpContext = null;
        }
    }
}

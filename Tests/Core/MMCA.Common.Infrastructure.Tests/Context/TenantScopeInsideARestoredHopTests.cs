using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Context;
using MMCA.Common.Infrastructure.Persistence.DataSources;

namespace MMCA.Common.Infrastructure.Tests.Context;

/// <summary>
/// M184 review follow-up: a scope created inside a restored hop inherits the hop's origin, but an
/// explicit <c>CreateTenantScope</c> target must win over it. Before the fix, a scope for another
/// tenant threw ("already ... cannot be changed") and a shared-target scope silently routed to the
/// hop's tenant.
/// </summary>
public sealed class TenantScopeInsideARestoredHopTests
{
    private static readonly DataSourceKey Source = DataSourceKey.Default(DataSource.SQLServer);

    private static ServiceProvider CreateProvider() => new ServiceCollection()
        .AddScoped<ITenantContext, TenantContext>()
        .AddScoped<ICorrelationContext, CorrelationContext>()
        .AddScoped<ScopedUserOverride>()
        .BuildServiceProvider();

    [Fact]
    public void CreateTenantScope_ForAnotherTenantInsideARestoredHop_UsesTheExplicitTenant()
    {
        using var provider = CreateProvider();
        using var hopScope = provider.CreateScope();
        using var origin = AmbientOrigin.Restore(hopScope.ServiceProvider, 7, null, "tenant-a", "corr-1", "Test");

        using var scope = provider.GetRequiredService<IServiceScopeFactory>()
            .CreateTenantScope(new TenantDataSourceTarget(Source, "tenant-b"));

        scope.ServiceProvider.GetRequiredService<ITenantContext>().TenantId.Should().Be("tenant-b");
    }

    [Fact]
    public void CreateTenantScope_ForTheSharedTargetInsideARestoredHop_StaysUnresolved()
    {
        using var provider = CreateProvider();
        using var hopScope = provider.CreateScope();
        using var origin = AmbientOrigin.Restore(hopScope.ServiceProvider, 7, null, "tenant-a", "corr-1", "Test");

        using var scope = provider.GetRequiredService<IServiceScopeFactory>()
            .CreateTenantScope(new TenantDataSourceTarget(Source, TenantId: null));

        scope.ServiceProvider.GetRequiredService<ITenantContext>().IsResolved.Should().BeFalse(
            "the shared target is an explicit choice of the shared source, not the hop's tenant");
    }

    [Fact]
    public void APlainScopeInsideARestoredHop_StillInheritsTheHopsTenant()
    {
        using var provider = CreateProvider();
        using var hopScope = provider.CreateScope();
        using var origin = AmbientOrigin.Restore(hopScope.ServiceProvider, 7, null, "tenant-a", "corr-1", "Test");

        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ITenantContext>().TenantId.Should().Be("tenant-a");
    }
}

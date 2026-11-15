using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.Administration;
using MMCA.Common.Application.Auth.Permissions;
using MMCA.Common.Infrastructure.Auth.Administration;
using MMCA.Common.Infrastructure.Persistence.Auth;
using MMCA.Common.Shared.Auth.Permissions;

namespace MMCA.Common.Infrastructure.Tests;

/// <summary>
/// Registration-level tests for <c>AddStoredPermissionGrants</c>: it decorates whatever
/// <see cref="IPermissionRegistry"/> the host registered (so compiled grants keep answering), falls
/// back to an empty compiled layer when nothing was registered yet, shares one cache instance between
/// the read side and the invalidator, and is safe to call twice.
/// </summary>
public sealed class DependencyInjectionStoredPermissionGrantsTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] overrides)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static ServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        return services;
    }

    [Fact]
    public void WithACompiledRegistry_DecoratesIt_SoCompiledGrantsStillAnswer()
    {
        var services = BaseServices();
        var compiled = new PermissionRegistry(new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Admin"] = new HashSet<string>(StringComparer.Ordinal) { "Orders.Read" },
        });
        services.AddSingleton<IPermissionRegistry>(compiled);

        services.AddStoredPermissionGrants(Configuration());

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IPermissionRegistry>();
        registry.Should().BeOfType<LayeredPermissionRegistry>();
        registry.HasPermission(["Admin"], "Orders.Read").Should().BeTrue("the compiled layer is consulted first");
        registry.HasPermission(["Admin"], "Orders.Delete").Should().BeFalse();
        services.Should().NotContain(
            d => d.ServiceType == typeof(IPermissionCatalog),
            "the empty fallback catalog is only registered when there was nothing to decorate");
    }

    [Fact]
    public void WithoutARegistry_RegistersAnEmptyCompiledLayer_AndItsCatalog()
    {
        var services = BaseServices();

        services.AddStoredPermissionGrants(Configuration());

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IPermissionRegistry>();
        registry.Should().BeOfType<LayeredPermissionRegistry>();
        registry.GetPermissions("Admin").Should().BeEmpty();
        registry.HasPermission(["Admin"], "Orders.Read").Should().BeFalse();
        provider.GetRequiredService<IPermissionCatalog>().Should().NotBeNull(
            "the administration surface needs a catalog to enumerate even with no compiled grants");
    }

    [Fact]
    public void RegistersTheStoreTheGateTheAdministrationServiceAndOneSharedCache()
    {
        var services = BaseServices();

        services.AddStoredPermissionGrants(Configuration());

        services.Should().Contain(d => d.ServiceType == typeof(PermissionGrantModelGate) && d.Lifetime == ServiceLifetime.Singleton);
        services.Should().Contain(d => d.ServiceType == typeof(IPermissionGrantStore)
            && d.ImplementationType == typeof(EFPermissionGrantStore) && d.Lifetime == ServiceLifetime.Scoped);
        services.Should().Contain(d => d.ServiceType == typeof(IRoleAdministrationService)
            && d.ImplementationType == typeof(StoredPermissionRoleAdministrationService) && d.Lifetime == ServiceLifetime.Scoped);

        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<IPermissionGrantCache>();
        var invalidator = provider.GetRequiredService<IPermissionGrantCacheInvalidator>();
        cache.Should().BeSameAs(invalidator, "an edit and the reads that follow it must see one snapshot");
        cache.Should().BeSameAs(provider.GetRequiredService<PermissionGrantCache>());
    }

    [Fact]
    public void CalledTwice_RegistersOneRefreshLoop()
    {
        var services = BaseServices();

        services.AddStoredPermissionGrants(Configuration());
        services.AddStoredPermissionGrants(Configuration());

        services.Count(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(PermissionGrantRefreshService))
            .Should().Be(1, "two modules opting in must not start two refresh loops in one process");
    }

    [Fact]
    public void BindsTheSettingsSection()
    {
        var services = BaseServices();

        services.AddStoredPermissionGrants(Configuration(
            ("Authentication:PermissionGrants:CacheSeconds", "120"),
            ("Authentication:PermissionGrants:DataSourceName", "Identity")));

        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<IOptions<PermissionGrantSettings>>().Value;
        settings.CacheSeconds.Should().Be(120);
        settings.DataSourceName.Should().Be("Identity");
    }

    [Fact]
    public void OutOfRangeSettings_FailValidation()
    {
        var services = BaseServices();

        services.AddStoredPermissionGrants(Configuration(("Authentication:PermissionGrants:CacheSeconds", "1")));

        using var provider = services.BuildServiceProvider();
        var act = () => provider.GetRequiredService<IOptions<PermissionGrantSettings>>().Value;

        act.Should().Throw<OptionsValidationException>("CacheSeconds is bounded to 5..3600 by data annotations");
    }
}

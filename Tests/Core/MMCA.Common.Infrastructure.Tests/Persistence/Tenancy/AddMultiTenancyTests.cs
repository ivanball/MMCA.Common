using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Infrastructure.Context;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Tenancy;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Tenancy;

/// <summary>
/// Coverage for the tenancy DI surface: what <c>AddMultiTenancy</c> binds, what startup validation
/// refuses, and the guarantee that the isolation machinery is registered whether or not the host
/// ever calls it.
/// </summary>
public sealed class AddMultiTenancyTests
{
    [Fact]
    public void TenancySettings_Defaults_AreOffAndFailClosed()
    {
        var settings = new TenancySettings();

        settings.Enabled.Should().BeFalse("adopting the framework must never start rejecting requests");
        settings.RequireTenant.Should().BeTrue("with tenancy on, an unscoped request reads across every tenant");
        settings.ClaimType.Should().Be("tenant_id");
        settings.HeaderName.Should().Be("X-Tenant-Id");
        settings.EffectiveResolutionOrder.Should().Equal(
            TenantResolutionStrategy.Claim, TenantResolutionStrategy.Header);
        settings.EffectiveExcludedPathPrefixes.Should().Equal("/health", "/alive", "/.well-known");
        settings.Tenants.Should().BeEmpty();
    }

    [Fact]
    public void WithoutTheSection_OptionsResolveToTheInertDefaults()
    {
        var services = new ServiceCollection();
        services.AddMultiTenancy(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<TenancySettings>>().Value.Enabled.Should().BeFalse();
    }

    [Fact]
    public void AddMultiTenancy_BindsTheTenancySection()
    {
        var services = new ServiceCollection();
        services.AddMultiTenancy(Configuration(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Tenancy:Enabled"] = "true",
            ["Tenancy:RequireTenant"] = "false",
            ["Tenancy:ClaimType"] = "org",
            ["Tenancy:HeaderName"] = "X-Org",
            ["Tenancy:ResolutionOrder:0"] = "Header",
            ["Tenancy:ExcludedPathPrefixes:0"] = "/ping",
        }));

        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<IOptions<TenancySettings>>().Value;

        settings.Enabled.Should().BeTrue();
        settings.RequireTenant.Should().BeFalse();
        settings.ClaimType.Should().Be("org");
        settings.HeaderName.Should().Be("X-Org");
        settings.EffectiveResolutionOrder.Should().ContainSingle(
            "a configured order REPLACES the framework default rather than extending it")
            .Which.Should().Be(TenantResolutionStrategy.Header);
        settings.EffectiveExcludedPathPrefixes.Should().Equal("/ping");
    }

    [Fact]
    public void AddMultiTenancy_BindsPerTenantDataSourceOverrides()
    {
        var services = new ServiceCollection();
        services.AddMultiTenancy(Configuration(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Tenancy:Tenants:acme:DataSources:Default:SqliteConnectionString"] = "DataSource=acme.db",
        }));

        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<IOptions<TenancySettings>>().Value;

        settings.Tenants["acme"].DataSources["Default"].SqliteConnectionString
            .Should().Be("DataSource=acme.db");
    }

    [Fact]
    public void AddMultiTenancy_CalledTwice_RegistersOneValidator()
    {
        var services = new ServiceCollection();
        services.AddMultiTenancy(new ConfigurationBuilder().Build());
        services.AddMultiTenancy(new ConfigurationBuilder().Build());

        services.Count(d => d.ServiceType == typeof(IValidateOptions<TenancySettings>)
            && d.ImplementationType == typeof(TenancySettingsValidator))
            .Should().Be(1);
    }

    // ── Validation ──
    [Fact]
    public void Validate_AcceptsAnOverrideOnAKnownPhysicalSource()
    {
        var settings = new TenancySettings();
        settings.Tenants["acme"] = TenantWithOverride(
            "Default", new TenantDataSourceOverrideSettings { SqliteConnectionString = "DataSource=acme.db" });

        Validate(settings).Failed.Should().BeFalse();
    }

    [Fact]
    public void Validate_RejectsAnOverrideOnAnUnknownSource()
    {
        var settings = new TenancySettings();
        settings.Tenants["acme"] = TenantWithOverride(
            "Nowhere", new TenantDataSourceOverrideSettings { SqliteConnectionString = "DataSource=acme.db" });

        var result = Validate(settings);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Nowhere",
            "an unknown name silently resolves to Default, which would put a tenant back on the shared database");
    }

    [Fact]
    public void Validate_RejectsAnOverrideWithNoConnectionString()
    {
        var settings = new TenancySettings();
        settings.Tenants["acme"] = TenantWithOverride("Default", new TenantDataSourceOverrideSettings());

        var result = Validate(settings);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("declares no connection string");
    }

    [Fact]
    public void Validate_RejectsTheHostStrategy()
    {
        var settings = new TenancySettings();
        settings.ResolutionOrder.Add(TenantResolutionStrategy.Host);

        var result = Validate(settings);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("not implemented");
    }

    [Fact]
    public void Validate_AcceptsClaimAndHeader()
    {
        var settings = new TenancySettings();
        settings.ResolutionOrder.Add(TenantResolutionStrategy.Header);
        settings.ResolutionOrder.Add(TenantResolutionStrategy.Claim);

        Validate(settings).Failed.Should().BeFalse();
    }

    [Fact]
    public void Validate_WithoutAResolver_SkipsTheSourceNameCheck()
    {
        var settings = new TenancySettings();
        settings.Tenants["acme"] = TenantWithOverride(
            "Nowhere", new TenantDataSourceOverrideSettings { SqliteConnectionString = "DataSource=acme.db" });

        new TenancySettingsValidator().Validate(null, settings).Failed.Should().BeFalse(
            "a container without persistence still gets the strategy and connection-string checks");
    }

    // ── Always-on registrations (isolation is never half-wired) ──
    [Fact]
    public void AddInfrastructure_RegistersTheTenantContextAndInterceptor_WithoutAddMultiTenancy()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(Configuration(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:SQLServerConnectionString"] = "Server=test;Database=test",
        }));

        services.Single(d => d.ServiceType == typeof(ITenantContext)).Lifetime
            .Should().Be(ServiceLifetime.Scoped);
        services.Single(d => d.ServiceType == typeof(ITenantContext)).ImplementationType
            .Should().Be<TenantContext>();
        services.Single(d => d.ServiceType == typeof(TenantSaveChangesInterceptor)).Lifetime
            .Should().Be(ServiceLifetime.Singleton);
    }

    private static TenantEntrySettings TenantWithOverride(
        string sourceName,
        TenantDataSourceOverrideSettings over)
    {
        var tenant = new TenantEntrySettings();
        tenant.DataSources[sourceName] = over;
        return tenant;
    }

    private static ValidateOptionsResult Validate(TenancySettings settings)
    {
        var resolver = new DataSourceResolver(
            Options.Create(new ConnectionStringSettings { SQLServerConnectionString = "Server=test;Database=test" }),
            new DataSourcesSettings(),
            NullLogger<DataSourceResolver>.Instance);

        return new TenancySettingsValidator(resolver).Validate(null, settings);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}

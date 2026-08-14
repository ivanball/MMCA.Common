using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Settings;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Tenancy;

/// <summary>
/// Coverage for the (source, tenant) expansion the background sweeps run on. A tenant with its own
/// database has its own outbox, inbox and trail tables, and nothing else opens that database, so
/// missing a pair means events that are never delivered and rows that are never purged.
/// </summary>
public sealed class TenantDataSourceTargetTests
{
    private static readonly DataSourceKey Default = DataSourceKey.Default(DataSource.SQLServer);
    private static readonly DataSourceKey Conference = new(DataSource.SQLServer, "Conference");

    [Fact]
    public void WithoutTenancy_EveryTargetIsTheSharedDatabase()
    {
        var targets = TenantDataSourceTargets.Expand([Default, Conference], settings: null);

        targets.Should().Equal(
            new TenantDataSourceTarget(Default, null),
            new TenantDataSourceTarget(Conference, null));
    }

    [Fact]
    public void SharedSchemaTenants_AddNoTargets()
    {
        var settings = new TenancySettings();
        settings.Tenants["acme"] = new TenantEntrySettings();

        var targets = TenantDataSourceTargets.Expand([Default], settings);

        targets.Should().ContainSingle("a shared-schema tenant's rows are drained by the shared sweep");
    }

    [Fact]
    public void ATenantWithItsOwnDatabase_AddsOnePairPerOverriddenSource()
    {
        var settings = new TenancySettings();
        settings.Tenants["acme"] = Tenant(("Conference", new TenantDataSourceOverrideSettings
        {
            SQLServerConnectionString = "Server=acme;Database=acme_conf",
        }));

        var targets = TenantDataSourceTargets.Expand([Default, Conference], settings);

        targets.Should().Equal(
            new TenantDataSourceTarget(Default, null),
            new TenantDataSourceTarget(Conference, null),
            new TenantDataSourceTarget(Conference, "acme"));
    }

    [Fact]
    public void AnOverrideForAnotherEngine_AddsNoPair()
    {
        var settings = new TenancySettings();
        settings.Tenants["acme"] = Tenant(("Default", new TenantDataSourceOverrideSettings
        {
            SqliteConnectionString = "DataSource=acme.db",
        }));

        var targets = TenantDataSourceTargets.Expand([Default], settings);

        targets.Should().ContainSingle("this host's Default source is SQL Server, which the tenant did not override");
    }

    [Fact]
    public void AnOverrideForASourceThisHostDoesNotOwn_AddsNoPair()
    {
        var settings = new TenancySettings();
        settings.Tenants["acme"] = Tenant(("Conference", new TenantDataSourceOverrideSettings
        {
            SQLServerConnectionString = "Server=acme;Database=acme_conf",
        }));

        var targets = TenantDataSourceTargets.Expand([Default], settings);

        targets.Should().ContainSingle("a host only ever drains its own databases");
    }

    [Fact]
    public void Target_RendersTheTenantForLogging()
    {
        new TenantDataSourceTarget(Default, null).ToString().Should().Be(Default.ToString());
        new TenantDataSourceTarget(Default, "acme").ToString().Should().Contain("acme");
    }

    // ── The two background services expose the same expansion ──
    [Fact]
    public void OutboxProcessor_EnumeratesThePairs()
    {
        var processor = new OutboxProcessor(
            Mock.Of<IServiceScopeFactory>(),
            NullLogger<OutboxProcessor>.Instance,
            Options.Create(new OutboxSettings()),
            Mock.Of<IOutboxSignal>(),
            RegistryWith(Default),
            ResolverFor(Default),
            TimeProvider.System,
            Options.Create(TenancyOverriding("Default")));

        processor.GetOutboxTargets().Should().Equal(
            new TenantDataSourceTarget(Default, null),
            new TenantDataSourceTarget(Default, "acme"));
    }

    [Fact]
    public void OutboxCleanupService_EnumeratesThePairs()
    {
        var cleanup = new OutboxCleanupService(
            Mock.Of<IServiceScopeFactory>(),
            NullLogger<OutboxCleanupService>.Instance,
            Options.Create(new OutboxSettings()),
            Options.Create(new MessageBusSettings()),
            RegistryWith(Default),
            ResolverFor(Default),
            TimeProvider.System,
            Options.Create(TenancyOverriding("Default")));

        cleanup.GetRelationalTargets().Should().Equal(
            new TenantDataSourceTarget(Default, null),
            new TenantDataSourceTarget(Default, "acme"));
    }

    [Fact]
    public void OutboxProcessor_WithoutTenancy_EnumeratesSourcesOnly()
    {
        var processor = new OutboxProcessor(
            Mock.Of<IServiceScopeFactory>(),
            NullLogger<OutboxProcessor>.Instance,
            Options.Create(new OutboxSettings()),
            Mock.Of<IOutboxSignal>(),
            RegistryWith(Default),
            ResolverFor(Default),
            TimeProvider.System);

        processor.GetOutboxTargets().Should().Equal(new TenantDataSourceTarget(Default, null));
    }

    private static TenantEntrySettings Tenant(params (string Source, TenantDataSourceOverrideSettings Override)[] overrides)
    {
        var tenant = new TenantEntrySettings();
        foreach (var (source, settings) in overrides)
        {
            tenant.DataSources[source] = settings;
        }

        return tenant;
    }

    private static TenancySettings TenancyOverriding(string sourceName)
    {
        var settings = new TenancySettings();
        settings.Tenants["acme"] = Tenant((sourceName, new TenantDataSourceOverrideSettings
        {
            SQLServerConnectionString = "Server=acme;Database=acme",
        }));
        return settings;
    }

    private static IEntityDataSourceRegistry RegistryWith(params DataSourceKey[] keys)
    {
        var registry = new Mock<IEntityDataSourceRegistry>();
        registry.Setup(r => r.GetPhysicalSourcesInUse()).Returns(keys);
        return registry.Object;
    }

    private static IDataSourceResolver ResolverFor(DataSourceKey key)
    {
        var resolver = new Mock<IDataSourceResolver>();
        resolver.Setup(r => r.ResolveLogical(It.IsAny<DataSource>(), It.IsAny<string>())).Returns(key);
        return resolver.Object;
    }
}

using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Services;
using MMCA.Common.Infrastructure.Settings;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Tenancy;

/// <summary>
/// Coverage for database-per-tenant routing in the scoped context factory: the override replaces the
/// connection while keeping the data source key, a tenant without an override stays on the shared
/// database, and a scope cannot switch tenants under a context already bound to one database.
/// </summary>
public sealed class DbContextFactoryTenantTests : IDisposable
{
    private const string Acme = "acme";
    private const string Globex = "globex";
    private const string AcmeConnection = "DataSource=acme-tenant.db";

    private static readonly DataSourceKey SqliteKey = DataSourceKey.Default(DataSource.Sqlite);

    private readonly SqliteConnection _connection = TenantTestContext.OpenDatabase();
    private readonly List<ApplicationDbContext> _created = [];

    public void Dispose()
    {
        foreach (var context in _created)
        {
            context.Dispose();
        }

        _connection.Dispose();
    }

    [Fact]
    public void OverriddenSource_IsCreatedAgainstTheTenantsConnection_WithTheSameKey()
    {
        var physicalFactory = PhysicalFactory();
        var sut = CreateSut(physicalFactory, Acme, TenancyWithAcmeOverride());

        sut.GetDbContext(SqliteKey);

        physicalFactory.Verify(
            f => f.Create(
                SqliteKey,
                It.Is<PhysicalDataSource>(p => p.Key == SqliteKey && p.ConnectionString == AcmeConnection)),
            Times.Once,
            "the key is what EF's model cache is keyed on, so only the connection may change");
        physicalFactory.Verify(f => f.Create(It.IsAny<DataSourceKey>()), Times.Never);
    }

    [Fact]
    public void TenantWithoutAnOverride_StaysOnTheSharedDatabase()
    {
        var physicalFactory = PhysicalFactory();
        var sut = CreateSut(physicalFactory, Globex, TenancyWithAcmeOverride());

        sut.GetDbContext(SqliteKey);

        physicalFactory.Verify(f => f.Create(SqliteKey), Times.Once,
            "a shared-schema tenant is isolated by the query filter, not by its own database");
        physicalFactory.Verify(
            f => f.Create(It.IsAny<DataSourceKey>(), It.IsAny<PhysicalDataSource>()), Times.Never);
    }

    [Fact]
    public void OverrideOnAnotherEngine_LeavesThisSourceShared()
    {
        var tenancy = new TenancySettings();
        var tenant = new TenantEntrySettings();
        tenant.DataSources["Default"] = new TenantDataSourceOverrideSettings
        {
            SQLServerConnectionString = "Server=acme;Database=acme",
        };
        tenancy.Tenants[Acme] = tenant;

        var physicalFactory = PhysicalFactory();
        var sut = CreateSut(physicalFactory, Acme, tenancy);

        sut.GetDbContext(SqliteKey);

        physicalFactory.Verify(f => f.Create(SqliteKey), Times.Once);
    }

    [Fact]
    public void NoTenancyConfigured_UsesTheOriginalCreateOverload()
    {
        var physicalFactory = PhysicalFactory();
        var sut = CreateSut(physicalFactory, tenantId: null, tenancy: null);

        sut.GetDbContext(SqliteKey);

        physicalFactory.Verify(f => f.Create(SqliteKey), Times.Once);
    }

    [Fact]
    public void RoutedContext_IsCachedForTheScope()
    {
        var physicalFactory = PhysicalFactory();
        var sut = CreateSut(physicalFactory, Acme, TenancyWithAcmeOverride());

        var first = sut.GetDbContext(SqliteKey);
        var second = sut.GetDbContext(SqliteKey);

        second.Should().BeSameAs(first);
        physicalFactory.Verify(
            f => f.Create(It.IsAny<DataSourceKey>(), It.IsAny<PhysicalDataSource>()), Times.Once);
    }

    [Fact]
    public void ChangingTheScopesTenant_AfterARoutedContextWasCreated_Throws()
    {
        var tenantContext = new MutableTenantContext(Acme);
        var sut = CreateSut(PhysicalFactory(), tenantContext, TenancyWithAcmeOverride());

        sut.GetDbContext(SqliteKey);
        tenantContext.Force(Globex);

        var act = () => sut.GetDbContext(SqliteKey);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*acme*globex*",
                "the cached context is bound to one physical database for the life of the scope");
    }

    [Fact]
    public void ChangingTheScopesTenant_OnASharedSource_IsAllowed()
    {
        var tenantContext = new MutableTenantContext(Globex);
        var sut = CreateSut(PhysicalFactory(), tenantContext, TenancyWithAcmeOverride());

        sut.GetDbContext(SqliteKey);
        tenantContext.Force("initech");

        var act = () => sut.GetDbContext(SqliteKey);

        act.Should().NotThrow("a shared source is scoped by the live filter, so the tenant may still move");
    }

    [Fact]
    public void CreatedContext_ReadsTheTenantLive()
    {
        var tenantContext = new MutableTenantContext(null);
        var sut = CreateSut(PhysicalFactory(), tenantContext, tenancy: null);

        var context = sut.GetDbContext(SqliteKey);
        context.CurrentTenantId.Should().BeNull();

        tenantContext.Force(Acme);

        context.CurrentTenantId.Should().Be(Acme,
            "the accessor is read at query time, which is what removes the middleware-ordering hazard");
    }

    [Fact]
    public void ResolvesFromAContainerThatNeverConfiguresTenancy()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton(PhysicalFactory().Object);
        services.AddSingleton(Mock.Of<IEntityDataSourceRegistry>());
        services.AddSingleton(Resolver());
        services.AddSingleton(Mock.Of<ICurrentUserService>());

        // AddInfrastructure registers ITenantContext unconditionally, and IOptions<TenancySettings>
        // falls back to a default-constructed instance, so a host that never calls AddMultiTenancy
        // still satisfies the constructor and simply resolves no tenant.
        services.AddScoped<ITenantContext, TenantContext>();

        using var provider = services.BuildServiceProvider();

        var sut = ActivatorUtilities.CreateInstance<DbContextFactory>(provider);

        sut.GetDbContext(SqliteKey).CurrentTenantId.Should().BeNull();
    }

    // ── Scaffolding ──
    private Mock<IPhysicalDbContextFactory> PhysicalFactory()
    {
        var factory = new Mock<IPhysicalDbContextFactory>();
        factory.Setup(f => f.Create(It.IsAny<DataSourceKey>())).Returns(NewContext);
        factory.Setup(f => f.Create(It.IsAny<DataSourceKey>(), It.IsAny<PhysicalDataSource>()))
            .Returns(NewContext);
        return factory;
    }

    private ApplicationDbContext NewContext()
    {
        var context = TenantTestContext.Create(_connection);
        _created.Add(context);
        return context;
    }

    private static TenancySettings TenancyWithAcmeOverride()
    {
        var settings = new TenancySettings();
        var tenant = new TenantEntrySettings();
        tenant.DataSources["Default"] = new TenantDataSourceOverrideSettings
        {
            SqliteConnectionString = AcmeConnection,
        };
        settings.Tenants[Acme] = tenant;
        return settings;
    }

    private static IDataSourceResolver Resolver()
    {
        var resolver = new Mock<IDataSourceResolver>();
        resolver.Setup(r => r.GetPhysical(It.IsAny<DataSourceKey>()))
            .Returns(TestDoubles.TestPhysicalDataSources.Sqlite());
        return resolver.Object;
    }

    private static DbContextFactory CreateSut(
        Mock<IPhysicalDbContextFactory> physicalFactory,
        string? tenantId,
        TenancySettings? tenancy)
    {
        var tenantContext = new MutableTenantContext(tenantId);
        return CreateSut(physicalFactory, tenantContext, tenancy);
    }

    private static DbContextFactory CreateSut(
        Mock<IPhysicalDbContextFactory> physicalFactory,
        ITenantContext tenantContext,
        TenancySettings? tenancy) =>
        new(
            physicalFactory.Object,
            Mock.Of<IEntityDataSourceRegistry>(),
            Resolver(),
            Mock.Of<ICurrentUserService>(),
            tenantContext,
            Options.Create(tenancy ?? new TenancySettings()));

    /// <summary>
    /// A tenant context a test can move, which the production <see cref="TenantContext"/> refuses to
    /// do. It is exactly that refusal the factory's guard exists to back up at a second layer.
    /// </summary>
    private sealed class MutableTenantContext(string? tenantId) : ITenantContext
    {
        public string? TenantId { get; private set; } = tenantId;

        public bool IsResolved => TenantId is not null;

        public void SetTenant(string tenantId) => TenantId = tenantId;

        public void Force(string? value) => TenantId = value;
    }
}

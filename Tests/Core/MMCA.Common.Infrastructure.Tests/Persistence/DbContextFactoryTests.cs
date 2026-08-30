using AwesomeAssertions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Settings;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

public sealed class DbContextFactoryTests
{
    // -- Constructor null guards (one per parameter) --
    [Fact]
    public void Constructor_NullPhysicalDbContextFactory_Throws()
    {
        var act = () => new DbContextFactory(
            null!,
            Mock.Of<IEntityDataSourceRegistry>(),
            new DefaultDataSourceResolver(),
            Mock.Of<ICurrentUserService>(),
            Mock.Of<ITenantContext>(),
            Options.Create(new TenancySettings()));

        act.Should().Throw<ArgumentNullException>().WithParameterName("physicalDbContextFactory");
    }

    [Fact]
    public void Constructor_NullEntityDataSourceRegistry_Throws()
    {
        var act = () => new DbContextFactory(
            Mock.Of<IPhysicalDbContextFactory>(),
            null!,
            new DefaultDataSourceResolver(),
            Mock.Of<ICurrentUserService>(),
            Mock.Of<ITenantContext>(),
            Options.Create(new TenancySettings()));

        act.Should().Throw<ArgumentNullException>().WithParameterName("entityDataSourceRegistry");
    }

    [Fact]
    public void Constructor_NullDataSourceResolver_Throws()
    {
        var act = () => new DbContextFactory(
            Mock.Of<IPhysicalDbContextFactory>(),
            Mock.Of<IEntityDataSourceRegistry>(),
            null!,
            Mock.Of<ICurrentUserService>(),
            Mock.Of<ITenantContext>(),
            Options.Create(new TenancySettings()));

        act.Should().Throw<ArgumentNullException>().WithParameterName("dataSourceResolver");
    }

    [Fact]
    public void Constructor_NullCurrentUserService_Throws()
    {
        var act = () => new DbContextFactory(
            Mock.Of<IPhysicalDbContextFactory>(),
            Mock.Of<IEntityDataSourceRegistry>(),
            new DefaultDataSourceResolver(),
            null!,
            Mock.Of<ITenantContext>(),
            Options.Create(new TenancySettings()));

        act.Should().Throw<ArgumentNullException>().WithParameterName("currentUserService");
    }

    // -- GetDbContext(DataSource) forwards to the engine's default physical source --
    [Fact]
    public void GetDbContext_SQLServer_CreatesDefaultSQLServerSource()
    {
        var physicalFactory = new Mock<IPhysicalDbContextFactory>();
        physicalFactory.Setup(f => f.Create(It.IsAny<DataSourceKey>())).Returns((ApplicationDbContext)null!);
        var sut = CreateSut(physicalFactory);

        sut.GetDbContext(DataSourceKey.Default(DataSource.SQLServer));

        physicalFactory.Verify(f => f.Create(DataSourceKey.Default(DataSource.SQLServer)), Times.Once);
    }

    [Fact]
    public void GetDbContext_Sqlite_CreatesDefaultSqliteSource()
    {
        var physicalFactory = new Mock<IPhysicalDbContextFactory>();
        physicalFactory.Setup(f => f.Create(It.IsAny<DataSourceKey>())).Returns((ApplicationDbContext)null!);
        var sut = CreateSut(physicalFactory);

        sut.GetDbContext(DataSourceKey.Default(DataSource.Sqlite));

        physicalFactory.Verify(f => f.Create(DataSourceKey.Default(DataSource.Sqlite)), Times.Once);
    }

    [Fact]
    public void GetDbContext_CosmosDB_CreatesDefaultCosmosSource()
    {
        var physicalFactory = new Mock<IPhysicalDbContextFactory>();
        physicalFactory.Setup(f => f.Create(It.IsAny<DataSourceKey>())).Returns((ApplicationDbContext)null!);
        var sut = CreateSut(physicalFactory);

        sut.GetDbContext(DataSourceKey.Default(DataSource.CosmosDB));

        physicalFactory.Verify(f => f.Create(DataSourceKey.Default(DataSource.CosmosDB)), Times.Once);
    }

    [Fact]
    public void GetDbContext_RecreatesContext_WhenCachedValueIsNull()
    {
        var physicalFactory = new Mock<IPhysicalDbContextFactory>();
        physicalFactory.Setup(f => f.Create(It.IsAny<DataSourceKey>())).Returns((ApplicationDbContext)null!);
        var sut = CreateSut(physicalFactory);

        _ = sut.GetDbContext(DataSourceKey.Default(DataSource.SQLServer));
        _ = sut.GetDbContext(DataSourceKey.Default(DataSource.SQLServer));

        // Source code re-creates when cached value is null
        physicalFactory.Verify(f => f.Create(DataSourceKey.Default(DataSource.SQLServer)), Times.Exactly(2));
    }

    [Fact]
    public void GetDbContext_AfterDispose_ThrowsObjectDisposedException()
    {
        var sut = CreateSut();
        sut.Dispose();

        var act = () => sut.GetDbContext(DataSourceKey.Default(DataSource.SQLServer));

        act.Should().Throw<ObjectDisposedException>();
    }

    // -- SaveChanges --
    [Fact]
    public void SaveChanges_WithNoContexts_ReturnsZero()
    {
        using var sut = CreateSut();

        var result = sut.SaveChanges();

        result.Should().Be(0);
    }

    // -- SaveChangesAsync --
    [Fact]
    public async Task SaveChangesAsync_WithNoContexts_ReturnsZero()
    {
        await using var sut = CreateSut();

        var result = await sut.SaveChangesAsync();

        result.Should().Be(0);
    }

    // -- Dispose --
    [Fact]
    public void Dispose_Twice_IsIdempotent()
    {
        var sut = CreateSut();

        sut.Dispose();
        var act = () => sut.Dispose();

        act.Should().NotThrow();
    }

    private static DbContextFactory CreateSut(Mock<IPhysicalDbContextFactory>? physicalFactory = null)
    {
        var registry = new Mock<IEntityDataSourceRegistry>();
        registry.Setup(r => r.GetPhysicalSourcesInUse()).Returns([]);

        return new DbContextFactory(
            (physicalFactory ?? new Mock<IPhysicalDbContextFactory>()).Object,
            registry.Object,
            new DefaultDataSourceResolver(),
            Mock.Of<ICurrentUserService>(),
            Mock.Of<ITenantContext>(),
            Options.Create(new TenancySettings()));
    }
}

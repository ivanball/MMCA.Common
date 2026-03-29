#pragma warning disable CA2000 // Dispose objects before losing scope — test doubles do not hold real resources

using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

public sealed class DbContextFactoryTests
{
    // -- Constructor null guard --
    [Fact]
    public void Constructor_NullCurrentUserService_Throws()
    {
        var act = () => new DbContextFactory(
            Mock.Of<IDbContextFactory<CosmosDbContext>>(),
            Mock.Of<IDbContextFactory<SqliteDbContext>>(),
            Mock.Of<IDbContextFactory<SQLServerDbContext>>(),
            null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // -- GetDbContext calls correct factory --
    [Fact]
    public void GetDbContext_SQLServer_CallsSQLServerFactory()
    {
        var sqlServerFactory = new Mock<IDbContextFactory<SQLServerDbContext>>();
        sqlServerFactory.Setup(f => f.CreateDbContext()).Returns((SQLServerDbContext)null!);
        var sut = CreateSut(sqlServerFactory: sqlServerFactory);

        sut.GetDbContext(DataSource.SQLServer);

        sqlServerFactory.Verify(f => f.CreateDbContext(), Times.Once);
    }

    [Fact]
    public void GetDbContext_Sqlite_CallsSqliteFactory()
    {
        var sqliteFactory = new Mock<IDbContextFactory<SqliteDbContext>>();
        sqliteFactory.Setup(f => f.CreateDbContext()).Returns((SqliteDbContext)null!);
        var sut = CreateSut(sqliteFactory: sqliteFactory);

        sut.GetDbContext(DataSource.Sqlite);

        sqliteFactory.Verify(f => f.CreateDbContext(), Times.Once);
    }

    [Fact]
    public void GetDbContext_CosmosDB_CallsCosmosFactory()
    {
        var cosmosFactory = new Mock<IDbContextFactory<CosmosDbContext>>();
        cosmosFactory.Setup(f => f.CreateDbContext()).Returns((CosmosDbContext)null!);
        var sut = CreateSut(cosmosFactory: cosmosFactory);

        sut.GetDbContext(DataSource.CosmosDB);

        cosmosFactory.Verify(f => f.CreateDbContext(), Times.Once);
    }

    [Fact]
    public void GetDbContext_RecreatesContext_WhenCachedValueIsNull()
    {
        var sqlServerFactory = new Mock<IDbContextFactory<SQLServerDbContext>>();
        sqlServerFactory.Setup(f => f.CreateDbContext()).Returns((SQLServerDbContext)null!);
        var sut = CreateSut(sqlServerFactory: sqlServerFactory);

        _ = sut.GetDbContext(DataSource.SQLServer);
        _ = sut.GetDbContext(DataSource.SQLServer);

        // Source code re-creates when cached value is null
        sqlServerFactory.Verify(f => f.CreateDbContext(), Times.Exactly(2));
    }

    [Fact]
    public void GetDbContext_AfterDispose_ThrowsObjectDisposedException()
    {
        var sut = CreateSut();
        sut.Dispose();

        var act = () => sut.GetDbContext(DataSource.SQLServer);

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

    // -- Null factory in registry throws on demand --
    [Fact]
    public void GetDbContext_NullCosmosFactory_ThrowsArgumentNullException()
    {
        using var sut = new DbContextFactory(
            null!,
            Mock.Of<IDbContextFactory<SqliteDbContext>>(),
            Mock.Of<IDbContextFactory<SQLServerDbContext>>(),
            Mock.Of<ICurrentUserService>());

        var act = () => sut.GetDbContext(DataSource.CosmosDB);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void GetDbContext_NullSqliteFactory_ThrowsArgumentNullException()
    {
        using var sut = new DbContextFactory(
            Mock.Of<IDbContextFactory<CosmosDbContext>>(),
            null!,
            Mock.Of<IDbContextFactory<SQLServerDbContext>>(),
            Mock.Of<ICurrentUserService>());

        var act = () => sut.GetDbContext(DataSource.Sqlite);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void GetDbContext_NullSQLServerFactory_ThrowsArgumentNullException()
    {
        using var sut = new DbContextFactory(
            Mock.Of<IDbContextFactory<CosmosDbContext>>(),
            Mock.Of<IDbContextFactory<SqliteDbContext>>(),
            null!,
            Mock.Of<ICurrentUserService>());

        var act = () => sut.GetDbContext(DataSource.SQLServer);

        act.Should().Throw<ArgumentNullException>();
    }

    private static DbContextFactory CreateSut(
        Mock<IDbContextFactory<CosmosDbContext>>? cosmosFactory = null,
        Mock<IDbContextFactory<SqliteDbContext>>? sqliteFactory = null,
        Mock<IDbContextFactory<SQLServerDbContext>>? sqlServerFactory = null) =>
        new(
            (cosmosFactory ?? new Mock<IDbContextFactory<CosmosDbContext>>()).Object,
            (sqliteFactory ?? new Mock<IDbContextFactory<SqliteDbContext>>()).Object,
            (sqlServerFactory ?? new Mock<IDbContextFactory<SQLServerDbContext>>()).Object,
            Mock.Of<ICurrentUserService>());
}

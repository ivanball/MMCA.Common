using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

public sealed class ApplicationDbContextEFFactoryTests
{
    // -- Constructor null guard --
    [Fact]
    public void Constructor_NullServiceProvider_Throws()
    {
        var configuration = new ConfigurationBuilder().Build();

        var act = () => new ApplicationDbContextEFFactory(null!, configuration);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullConfiguration_DefaultsToSQLServer()
    {
        var mockSqlServerFactory = new Mock<IDbContextFactory<SQLServerDbContext>>();
        mockSqlServerFactory.Setup(f => f.CreateDbContext()).Returns((SQLServerDbContext)null!);
        var serviceProvider = BuildServiceProvider(sqlServerFactory: mockSqlServerFactory.Object);

        var sut = new ApplicationDbContextEFFactory(serviceProvider, null!);
        _ = sut.CreateDbContext();

        mockSqlServerFactory.Verify(f => f.CreateDbContext(), Times.Once);
    }

    // -- CreateDbContext branches --
    [Theory]
    [InlineData("SQLServer")]
    [InlineData("sqlserver")]
    public void CreateDbContext_SQLServer_DelegatesToSQLServerFactory(string dataSourceValue)
    {
        var mockSqlServerFactory = new Mock<IDbContextFactory<SQLServerDbContext>>();
        mockSqlServerFactory.Setup(f => f.CreateDbContext()).Returns((SQLServerDbContext)null!);
        var serviceProvider = BuildServiceProvider(sqlServerFactory: mockSqlServerFactory.Object);
        var configuration = BuildConfiguration("DataSource", dataSourceValue);

        var sut = new ApplicationDbContextEFFactory(serviceProvider, configuration);
        _ = sut.CreateDbContext();

        mockSqlServerFactory.Verify(f => f.CreateDbContext(), Times.Once);
    }

    [Fact]
    public void CreateDbContext_Sqlite_DelegatesToSqliteFactory()
    {
        var mockSqliteFactory = new Mock<IDbContextFactory<SqliteDbContext>>();
        mockSqliteFactory.Setup(f => f.CreateDbContext()).Returns((SqliteDbContext)null!);
        var serviceProvider = BuildServiceProvider(sqliteFactory: mockSqliteFactory.Object);
        var configuration = BuildConfiguration("DataSource", "Sqlite");

        var sut = new ApplicationDbContextEFFactory(serviceProvider, configuration);
        _ = sut.CreateDbContext();

        mockSqliteFactory.Verify(f => f.CreateDbContext(), Times.Once);
    }

    [Fact]
    public void CreateDbContext_CosmosDB_DelegatesToCosmosFactory()
    {
        var mockCosmosFactory = new Mock<IDbContextFactory<CosmosDbContext>>();
        mockCosmosFactory.Setup(f => f.CreateDbContext()).Returns((CosmosDbContext)null!);
        var serviceProvider = BuildServiceProvider(cosmosFactory: mockCosmosFactory.Object);
        var configuration = BuildConfiguration("DataSource", "CosmosDB");

        var sut = new ApplicationDbContextEFFactory(serviceProvider, configuration);
        _ = sut.CreateDbContext();

        mockCosmosFactory.Verify(f => f.CreateDbContext(), Times.Once);
    }

    [Fact]
    public void CreateDbContext_InvalidDataSource_DefaultsToSQLServer()
    {
        var mockSqlServerFactory = new Mock<IDbContextFactory<SQLServerDbContext>>();
        mockSqlServerFactory.Setup(f => f.CreateDbContext()).Returns((SQLServerDbContext)null!);
        var serviceProvider = BuildServiceProvider(sqlServerFactory: mockSqlServerFactory.Object);
        var configuration = BuildConfiguration("DataSource", "NotAValidDataSource");

        var sut = new ApplicationDbContextEFFactory(serviceProvider, configuration);
        _ = sut.CreateDbContext();

        mockSqlServerFactory.Verify(f => f.CreateDbContext(), Times.Once);
    }

    [Fact]
    public void CreateDbContext_DefaultDataSourceKey_TakesPrecedenceOverDataSource()
    {
        var mockSqliteFactory = new Mock<IDbContextFactory<SqliteDbContext>>();
        mockSqliteFactory.Setup(f => f.CreateDbContext()).Returns((SqliteDbContext)null!);
        var serviceProvider = BuildServiceProvider(sqliteFactory: mockSqliteFactory.Object);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DefaultDataSource"] = "Sqlite",
                ["DataSource"] = "SQLServer",
            })
            .Build();

        var sut = new ApplicationDbContextEFFactory(serviceProvider, configuration);
        _ = sut.CreateDbContext();

        mockSqliteFactory.Verify(f => f.CreateDbContext(), Times.Once);
    }

    private static IConfiguration BuildConfiguration(string key, string value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

    private static IServiceProvider BuildServiceProvider(
        IDbContextFactory<SQLServerDbContext>? sqlServerFactory = null,
        IDbContextFactory<SqliteDbContext>? sqliteFactory = null,
        IDbContextFactory<CosmosDbContext>? cosmosFactory = null)
    {
        var mock = new Mock<IServiceProvider>();

        if (sqlServerFactory is not null)
        {
            mock.Setup(sp => sp.GetService(typeof(IDbContextFactory<SQLServerDbContext>)))
                .Returns(sqlServerFactory);
        }

        if (sqliteFactory is not null)
        {
            mock.Setup(sp => sp.GetService(typeof(IDbContextFactory<SqliteDbContext>)))
                .Returns(sqliteFactory);
        }

        if (cosmosFactory is not null)
        {
            mock.Setup(sp => sp.GetService(typeof(IDbContextFactory<CosmosDbContext>)))
                .Returns(cosmosFactory);
        }

        return mock.Object;
    }
}

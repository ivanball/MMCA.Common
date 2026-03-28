using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Repositories.Factory;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

public sealed class UnitOfWorkTests
{
    private sealed record Mocks(
        Mock<IDbContextFactory> DbContextFactory,
        Mock<IDataSourceService> DataSourceService,
        Mock<IRepositoryFactory> RepositoryFactory);

    private static (UnitOfWork Sut, Mocks Mocks) CreateSut()
    {
        var dbContextFactory = new Mock<IDbContextFactory>();
        var dataSourceService = new Mock<IDataSourceService>();
        var repositoryFactory = new Mock<IRepositoryFactory>();

        var sut = new UnitOfWork(dbContextFactory.Object, dataSourceService.Object, repositoryFactory.Object);
        return (sut, new Mocks(dbContextFactory, dataSourceService, repositoryFactory));
    }

    [Fact]
    public async Task SaveChangesAsync_DelegatesToDbContextFactory()
    {
        var (sut, mocks) = CreateSut();
        mocks.DbContextFactory.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(5);

        var result = await sut.SaveChangesAsync();

        result.Should().Be(5);
    }

    [Fact]
    public void Save_DelegatesToDbContextFactory()
    {
        var (sut, mocks) = CreateSut();
        mocks.DbContextFactory.Setup(x => x.SaveChanges()).Returns(3);

        var result = sut.Save();

        result.Should().Be(3);
    }

    [Fact]
    public void BeginTransaction_DelegatesToDbContextFactory()
    {
        var (sut, mocks) = CreateSut();

        sut.BeginTransaction();

        mocks.DbContextFactory.Verify(x => x.BeginTransaction(), Times.Once);
    }

    [Fact]
    public void CommitTransaction_DelegatesToDbContextFactory()
    {
        var (sut, mocks) = CreateSut();

        sut.CommitTransaction();

        mocks.DbContextFactory.Verify(x => x.CommitTransaction(), Times.Once);
    }

    [Fact]
    public void RollbackTransaction_DelegatesToDbContextFactory()
    {
        var (sut, mocks) = CreateSut();

        sut.RollbackTransaction();

        mocks.DbContextFactory.Verify(x => x.RollbackTransaction(), Times.Once);
    }

    [Fact]
    public void Dispose_DisposesDbContextFactory()
    {
        var (sut, mocks) = CreateSut();

        sut.Dispose();

        mocks.DbContextFactory.Verify(x => x.Dispose(), Times.Once);
    }

    [Fact]
    public void Dispose_Twice_IsIdempotent()
    {
        var (sut, mocks) = CreateSut();

        sut.Dispose();
        sut.Dispose();

        mocks.DbContextFactory.Verify(x => x.Dispose(), Times.Once);
    }

    [Fact]
    public void GetRepository_CachesResult()
    {
        var (sut, mocks) = CreateSut();
        var mockRepo = new Mock<IRepository<FakeAggregate, int>>();

        mocks.DataSourceService.Setup(x => x.GetDataSource(typeof(FakeAggregate))).Returns(DataSource.SQLServer);
        mocks.DbContextFactory.Setup(x => x.GetDbContext(DataSource.SQLServer)).Returns((ApplicationDbContext)null!);
        mocks.RepositoryFactory.Setup(x => x.Create<FakeAggregate, int>(It.IsAny<DbContext>())).Returns(mockRepo.Object);

        var repo1 = sut.GetRepository<FakeAggregate, int>();
        var repo2 = sut.GetRepository<FakeAggregate, int>();

        repo1.Should().BeSameAs(repo2);
        mocks.RepositoryFactory.Verify(x => x.Create<FakeAggregate, int>(It.IsAny<DbContext>()), Times.Once);
    }

    [Fact]
    public void GetReadRepository_CachesResult()
    {
        var (sut, mocks) = CreateSut();
        var mockRepo = new Mock<IReadRepository<FakeEntity, int>>();

        mocks.DataSourceService.Setup(x => x.GetDataSource(typeof(FakeEntity))).Returns(DataSource.SQLServer);
        mocks.DbContextFactory.Setup(x => x.GetDbContext(DataSource.SQLServer)).Returns((ApplicationDbContext)null!);
        mocks.RepositoryFactory.Setup(x => x.CreateReadOnly<FakeEntity, int>(It.IsAny<DbContext>())).Returns(mockRepo.Object);

        var repo1 = sut.GetReadRepository<FakeEntity, int>();
        var repo2 = sut.GetReadRepository<FakeEntity, int>();

        repo1.Should().BeSameAs(repo2);
        mocks.RepositoryFactory.Verify(x => x.CreateReadOnly<FakeEntity, int>(It.IsAny<DbContext>()), Times.Once);
    }

    [Fact]
    public void Constructor_WithNullDbContextFactory_Throws()
    {
        var act = () => new UnitOfWork(null!, new Mock<IDataSourceService>().Object, new Mock<IRepositoryFactory>().Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_WithNullRepositoryFactory_Throws()
    {
        var act = () => new UnitOfWork(new Mock<IDbContextFactory>().Object, new Mock<IDataSourceService>().Object, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    public sealed class FakeAggregate : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class FakeEntity : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }
}

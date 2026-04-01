using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Repositories.Factory;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

public sealed class UnitOfWorkAdditionalTests
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
    public void RequestIdentityInsert_DelegatesToDbContextFactory()
    {
        var (sut, mocks) = CreateSut();

        sut.RequestIdentityInsert();

        mocks.DbContextFactory.Verify(x => x.RequestIdentityInsert(), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_DisposesDbContextFactory()
    {
        var (sut, mocks) = CreateSut();

        await sut.DisposeAsync();

        mocks.DbContextFactory.Verify(x => x.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_Twice_IsIdempotent()
    {
        var (sut, mocks) = CreateSut();

        await sut.DisposeAsync();
        await sut.DisposeAsync();

        mocks.DbContextFactory.Verify(x => x.DisposeAsync(), Times.Once);
    }

    [Fact]
    public void GetRepository_UsesDataSourceServiceAndDbContextFactory()
    {
        var (sut, mocks) = CreateSut();
        var mockRepo = new Mock<IRepository<FakeAggregate, int>>();

        mocks.DataSourceService.Setup(x => x.GetDataSource(typeof(FakeAggregate))).Returns(DataSource.Sqlite);
        mocks.DbContextFactory.Setup(x => x.GetDbContext(DataSource.Sqlite)).Returns((MMCA.Common.Infrastructure.Persistence.DbContexts.ApplicationDbContext)null!);
        mocks.RepositoryFactory.Setup(x => x.Create<FakeAggregate, int>(It.IsAny<Microsoft.EntityFrameworkCore.DbContext>())).Returns(mockRepo.Object);

        var repo = sut.GetRepository<FakeAggregate, int>();

        repo.Should().BeSameAs(mockRepo.Object);
        mocks.DataSourceService.Verify(x => x.GetDataSource(typeof(FakeAggregate)), Times.Once);
        mocks.DbContextFactory.Verify(x => x.GetDbContext(DataSource.Sqlite), Times.Once);
    }

    [Fact]
    public void GetReadRepository_UsesDataSourceServiceAndDbContextFactory()
    {
        var (sut, mocks) = CreateSut();
        var mockRepo = new Mock<IReadRepository<FakeEntity, int>>();

        mocks.DataSourceService.Setup(x => x.GetDataSource(typeof(FakeEntity))).Returns(DataSource.CosmosDB);
        mocks.DbContextFactory.Setup(x => x.GetDbContext(DataSource.CosmosDB)).Returns((MMCA.Common.Infrastructure.Persistence.DbContexts.ApplicationDbContext)null!);
        mocks.RepositoryFactory.Setup(x => x.CreateReadOnly<FakeEntity, int>(It.IsAny<Microsoft.EntityFrameworkCore.DbContext>())).Returns(mockRepo.Object);

        var repo = sut.GetReadRepository<FakeEntity, int>();

        repo.Should().BeSameAs(mockRepo.Object);
        mocks.DataSourceService.Verify(x => x.GetDataSource(typeof(FakeEntity)), Times.Once);
        mocks.DbContextFactory.Verify(x => x.GetDbContext(DataSource.CosmosDB), Times.Once);
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

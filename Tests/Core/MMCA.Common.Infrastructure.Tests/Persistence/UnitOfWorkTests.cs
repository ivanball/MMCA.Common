using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
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

    // -- The unit of work classifies its own lost insert races (O-31) --
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsUniqueConstraintViolation_AnswersThroughTheRegisteredDetector(bool isViolation)
    {
        var exception = new InvalidOperationException("save failed");
        var detector = new Mock<IUniqueConstraintViolationDetector>();
        detector.Setup(d => d.IsUniqueConstraintViolation(exception)).Returns(isViolation);
        using var sut = new UnitOfWork(
            new Mock<IDbContextFactory>().Object,
            new Mock<IDataSourceService>().Object,
            new Mock<IRepositoryFactory>().Object,
            detector.Object);

        sut.Should().BeAssignableTo<IUniqueConstraintViolationDetector>(
            "AddChildEntityHandlerBase reaches the detector through the unit of work it already holds");
        sut.IsUniqueConstraintViolation(exception).Should().Be(isViolation);
    }

    [Fact]
    public void IsUniqueConstraintViolation_WithNoDetector_ClassifiesNothing()
    {
        var (sut, _) = CreateSut();

        sut.IsUniqueConstraintViolation(new InvalidOperationException("save failed")).Should().BeFalse();
        sut.Dispose();
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

        mocks.DataSourceService.Setup(x => x.GetDataSourceKey(typeof(FakeAggregate))).Returns(DataSourceKey.Default(DataSource.SQLServer));
        mocks.DbContextFactory.Setup(x => x.GetDbContext(DataSourceKey.Default(DataSource.SQLServer))).Returns((ApplicationDbContext)null!);
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

        mocks.DataSourceService.Setup(x => x.GetDataSourceKey(typeof(FakeEntity))).Returns(DataSourceKey.Default(DataSource.SQLServer));
        mocks.DbContextFactory.Setup(x => x.GetDbContext(DataSourceKey.Default(DataSource.SQLServer))).Returns((ApplicationDbContext)null!);
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

    [Fact]
    public void RequestExplicitKeyInsert_DelegatesToDbContextFactory()
    {
        var (sut, mocks) = CreateSut();

        sut.RequestExplicitKeyInsert();

        mocks.DbContextFactory.Verify(x => x.RequestExplicitKeyInsert(), Times.Once);
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

        mocks.DataSourceService.Setup(x => x.GetDataSourceKey(typeof(FakeAggregate))).Returns(DataSourceKey.Default(DataSource.Sqlite));
        mocks.DbContextFactory.Setup(x => x.GetDbContext(DataSourceKey.Default(DataSource.Sqlite))).Returns((MMCA.Common.Infrastructure.Persistence.DbContexts.ApplicationDbContext)null!);
        mocks.RepositoryFactory.Setup(x => x.Create<FakeAggregate, int>(It.IsAny<Microsoft.EntityFrameworkCore.DbContext>())).Returns(mockRepo.Object);

        var repo = sut.GetRepository<FakeAggregate, int>();

        repo.Should().BeSameAs(mockRepo.Object);
        mocks.DataSourceService.Verify(x => x.GetDataSourceKey(typeof(FakeAggregate)), Times.Once);
        mocks.DbContextFactory.Verify(x => x.GetDbContext(DataSourceKey.Default(DataSource.Sqlite)), Times.Once);
    }

    [Fact]
    public void GetReadRepository_UsesDataSourceServiceAndDbContextFactory()
    {
        var (sut, mocks) = CreateSut();
        var mockRepo = new Mock<IReadRepository<FakeEntity, int>>();

        mocks.DataSourceService.Setup(x => x.GetDataSourceKey(typeof(FakeEntity))).Returns(DataSourceKey.Default(DataSource.CosmosDB));
        mocks.DbContextFactory.Setup(x => x.GetDbContext(DataSourceKey.Default(DataSource.CosmosDB))).Returns((MMCA.Common.Infrastructure.Persistence.DbContexts.ApplicationDbContext)null!);
        mocks.RepositoryFactory.Setup(x => x.CreateReadOnly<FakeEntity, int>(It.IsAny<Microsoft.EntityFrameworkCore.DbContext>())).Returns(mockRepo.Object);

        var repo = sut.GetReadRepository<FakeEntity, int>();

        repo.Should().BeSameAs(mockRepo.Object);
        mocks.DataSourceService.Verify(x => x.GetDataSourceKey(typeof(FakeEntity)), Times.Once);
        mocks.DbContextFactory.Verify(x => x.GetDbContext(DataSourceKey.Default(DataSource.CosmosDB)), Times.Once);
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

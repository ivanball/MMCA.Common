using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Settings;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.Repositories;
using MMCA.Common.Infrastructure.Persistence.Repositories.Factory;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

public sealed class RepositoryFactoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContext _context;
    private readonly ServiceProvider _serviceProvider;

    public RepositoryFactoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new TestDbContext(options);
        _context.Database.EnsureCreated();

        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void Create_WithMiniProfilerDisabled_ReturnsEFRepository()
    {
        var settings = new Mock<IApplicationSettings>();
        settings.Setup(s => s.UseMiniProfiler).Returns(false);

        var sut = new RepositoryFactory(_serviceProvider, settings.Object);

        var repo = sut.Create<FakeAggregate, int>(_context);

        repo.Should().NotBeNull();
        repo.Should().BeOfType<EFRepository<FakeAggregate, int>>();
    }

    [Fact]
    public void Create_WithMiniProfilerEnabled_ReturnsDecoratedRepository()
    {
        var settings = new Mock<IApplicationSettings>();
        settings.Setup(s => s.UseMiniProfiler).Returns(true);

        var sut = new RepositoryFactory(_serviceProvider, settings.Object);

        var repo = sut.Create<FakeAggregate, int>(_context);

        repo.Should().NotBeNull();
        repo.Should().BeOfType<EFRepositoryDecorator<FakeAggregate, int>>();
    }

    [Fact]
    public void CreateReadOnly_WithMiniProfilerDisabled_ReturnsEFReadRepository()
    {
        var settings = new Mock<IApplicationSettings>();
        settings.Setup(s => s.UseMiniProfiler).Returns(false);

        var sut = new RepositoryFactory(_serviceProvider, settings.Object);

        var repo = sut.CreateReadOnly<FakeEntity, int>(_context);

        repo.Should().NotBeNull();
        repo.Should().BeOfType<EFReadRepository<FakeEntity, int>>();
    }

    [Fact]
    public void CreateReadOnly_WithMiniProfilerEnabled_ReturnsDecoratedReadRepository()
    {
        var settings = new Mock<IApplicationSettings>();
        settings.Setup(s => s.UseMiniProfiler).Returns(true);

        var sut = new RepositoryFactory(_serviceProvider, settings.Object);

        var repo = sut.CreateReadOnly<FakeEntity, int>(_context);

        repo.Should().NotBeNull();
        repo.Should().BeOfType<EFReadRepositoryDecorator<FakeEntity, int>>();
    }

    [Fact]
    public void Create_ReturnsFunctionalRepository()
    {
        var settings = new Mock<IApplicationSettings>();
        settings.Setup(s => s.UseMiniProfiler).Returns(false);

        var sut = new RepositoryFactory(_serviceProvider, settings.Object);
        var repo = sut.Create<FakeAggregate, int>(_context);

        repo.Should().BeAssignableTo<IRepository<FakeAggregate, int>>();
    }

    [Fact]
    public void CreateReadOnly_ReturnsFunctionalReadRepository()
    {
        var settings = new Mock<IApplicationSettings>();
        settings.Setup(s => s.UseMiniProfiler).Returns(false);

        var sut = new RepositoryFactory(_serviceProvider, settings.Object);
        var repo = sut.CreateReadOnly<FakeEntity, int>(_context);

        repo.Should().BeAssignableTo<IReadRepository<FakeEntity, int>>();
    }

    public sealed class FakeAggregate : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class FakeEntity : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class TestDbContext(DbContextOptions options) : DbContext(options)
    {
        public DbSet<FakeAggregate> FakeAggregates => Set<FakeAggregate>();

        public DbSet<FakeEntity> FakeEntities => Set<FakeEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<FakeAggregate>(b =>
            {
                b.HasKey(e => e.Id);
                b.Property(e => e.Id).ValueGeneratedNever();
            });
            modelBuilder.Entity<FakeEntity>(b =>
            {
                b.HasKey(e => e.Id);
                b.Property(e => e.Id).ValueGeneratedNever();
            });
        }
    }
}

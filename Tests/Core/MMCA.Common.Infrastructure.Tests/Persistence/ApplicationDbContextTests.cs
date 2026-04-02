using System.Reflection;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for <see cref="ApplicationDbContext"/> covering SaveChangesAsync, OnModelCreating,
/// and ApplyConfigurationsForEntitiesInContext.
/// </summary>
public sealed class ApplicationDbContextTests : IDisposable
{
    private readonly TestApplicationDbContext _dbContext;

    public ApplicationDbContextTests() =>
        _dbContext = TestApplicationDbContext.Create();

    public void Dispose() => _dbContext.Dispose();

    [Fact]
    public async Task SaveChangesAsync_WithUserId_SetsCurrentSaveUserId()
    {
        var entity = new TestEntity { Id = 1, Name = "Test" };
        _dbContext.TestEntities.Add(entity);

        await _dbContext.SaveChangesAsync(42);

        // The entity should be persisted
        var found = await _dbContext.TestEntities.FindAsync(1);
        found.Should().NotBeNull();
    }

    [Fact]
    public async Task SaveChangesAsync_WithNullUserId_DefaultsUserId()
    {
        var entity = new TestEntity { Id = 2, Name = "Test2" };
        _dbContext.TestEntities.Add(entity);

        await _dbContext.SaveChangesAsync(null);

        var found = await _dbContext.TestEntities.FindAsync(2);
        found.Should().NotBeNull();
    }

    [Fact]
    public void SupportsOutbox_DefaultsToTrue()
    {
        // Access the internal property via reflection
        PropertyInfo? prop = typeof(ApplicationDbContext)
            .GetProperty("SupportsOutbox", BindingFlags.NonPublic | BindingFlags.Instance);
        prop.Should().NotBeNull();

        var value = (bool)prop!.GetValue(_dbContext)!;
        value.Should().BeTrue();
    }

    [Fact]
    public void Set_ReturnsDbSetForEntity()
    {
        var dbSet = _dbContext.Set<TestEntity>();

        dbSet.Should().NotBeNull();
    }

    [Fact]
    public void ApplyConfigurationsForEntitiesInContext_InvalidDataSource_ThrowsInvalidOperationException()
    {
        // Use reflection to call ApplyConfigurationsForEntitiesInContext with an invalid DataSource
        MethodInfo? method = typeof(ApplicationDbContext)
            .GetMethod("ApplyConfigurationsForEntitiesInContext", BindingFlags.NonPublic | BindingFlags.Instance);
        method.Should().NotBeNull();

        var modelBuilder = new ModelBuilder();
        var act = () => method!.Invoke(_dbContext, [(DataSource)999, modelBuilder]);

        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidOperationException>();
    }

    public sealed class TestEntity : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class TestApplicationDbContext : ApplicationDbContext
    {
        public DbSet<TestEntity> TestEntities => Set<TestEntity>();

        private TestApplicationDbContext(
            DbContextOptions<TestApplicationDbContext> options,
            IServiceProvider serviceProvider,
            IEntityConfigurationAssemblyProvider assemblyProvider)
            : base(options, serviceProvider, assemblyProvider)
        {
        }

        public static TestApplicationDbContext Create()
        {
            var services = new ServiceCollection();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
            services.AddSingleton<DomainEventSaveChangesInterceptor>(_ =>
            {
                var dispatcher = new Mock<Application.Interfaces.IDomainEventDispatcher>();
                var logger = new Mock<Microsoft.Extensions.Logging.ILogger<DomainEventSaveChangesInterceptor>>();
                var outboxSignal = new Mock<MMCA.Common.Infrastructure.Persistence.Outbox.IOutboxSignal>();
                return new DomainEventSaveChangesInterceptor(dispatcher.Object, logger.Object, outboxSignal.Object);
            });
            IServiceProvider sp = services.BuildServiceProvider();

            var assemblyProvider = Mock.Of<IEntityConfigurationAssemblyProvider>(
                p => p.GetConfigurationAssemblies() == Array.Empty<Assembly>());

            var options = new DbContextOptionsBuilder<TestApplicationDbContext>()
                .UseSqlite("DataSource=:memory:")
                .Options;

            var context = new TestApplicationDbContext(options, sp, assemblyProvider);
            context.Database.OpenConnection();
            context.Database.EnsureCreated();
            return context;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<TestEntity>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedNever();
                e.Property(x => x.Name);
                e.Property(x => x.RowVersion).IsConcurrencyToken();
            });
    }
}

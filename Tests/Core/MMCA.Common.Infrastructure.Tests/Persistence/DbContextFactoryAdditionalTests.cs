using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

public sealed class DbContextFactoryAdditionalTests
{
    // -- DisposeAsync --
    [Fact]
    public async Task DisposeAsync_AfterDispose_GetDbContext_ThrowsObjectDisposedException()
    {
        var sut = CreateSut();
        await sut.DisposeAsync();

        var act = () => sut.GetDbContext(DataSource.SQLServer);

        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task DisposeAsync_Twice_IsIdempotent()
    {
        var sut = CreateSut();

        await sut.DisposeAsync();
        var act = async () => await sut.DisposeAsync();

        await act.Should().NotThrowAsync();
    }

    // -- RequestIdentityInsert --
    [Fact]
    public async Task RequestIdentityInsert_WithNoContexts_SaveReturnsZero()
    {
        await using var sut = CreateSut();

        sut.RequestIdentityInsert();
        var result = await sut.SaveChangesAsync();

        result.Should().Be(0);
    }

    // -- BeginTransaction / CommitTransaction / RollbackTransaction --
    [Fact]
    public void BeginTransaction_WithNoContexts_DoesNotThrow()
    {
        using var sut = CreateSut();

        var act = () => sut.BeginTransaction();

        act.Should().NotThrow();
    }

    [Fact]
    public void CommitTransaction_WithNoContexts_DoesNotThrow()
    {
        using var sut = CreateSut();

        var act = () => sut.CommitTransaction();

        act.Should().NotThrow();
    }

    [Fact]
    public void RollbackTransaction_WithNoContexts_DoesNotThrow()
    {
        using var sut = CreateSut();

        var act = () => sut.RollbackTransaction();

        act.Should().NotThrow();
    }

    // -- EnsureCreatedAsync --
    [Fact]
    public async Task EnsureCreatedAsync_WithNoSourcesInUse_CompletesSuccessfully()
    {
        await using var sut = CreateSut();

        var act = async () => await sut.EnsureCreatedAsync();

        await act.Should().NotThrowAsync();
    }

    // -- MigrateAsync --
    [Fact]
    public async Task MigrateAsync_WithNoSourcesInUse_CompletesSuccessfully()
    {
        await using var sut = CreateSut();

        var act = async () => await sut.MigrateAsync();

        await act.Should().NotThrowAsync();
    }

    // -- HasPendingMigrationsAsync --
    [Fact]
    public async Task HasPendingMigrationsAsync_WithNoSourcesInUse_ReturnsFalse()
    {
        await using var sut = CreateSut();

        var result = await sut.HasPendingMigrationsAsync();

        result.Should().BeFalse();
    }

    // -- Saving must tolerate a context being created mid-save --
    // SaveChangesAsync dispatches domain events in-process, and a handler that reaches a data
    // source not yet materialized calls GetDbContext, which adds to the factory's dictionary. The
    // loop used to enumerate that dictionary directly, so the handler's call threw
    // "Collection was modified" and took the whole save down with it.
    [Fact]
    public async Task SaveChangesAsync_WhenSavingMaterializesAnotherContext_DoesNotThrow()
    {
        var physicalFactory = new Mock<IPhysicalDbContextFactory>();
        DbContextFactory? sut = null;
        var secondaryKey = new DataSourceKey(DataSource.Sqlite, "Secondary");

        physicalFactory
            .Setup(f => f.Create(It.IsAny<DataSourceKey>()))
            .Returns<DataSourceKey>(key =>
            {
                if (key == secondaryKey)
                {
                    return MidSaveContextCreatingDbContext.CreateInert();
                }

                // The primary context reaches for a second source while it is being saved.
                return MidSaveContextCreatingDbContext.CreateThatResolves(() => sut!.GetDbContext(secondaryKey));
            });

        await using var factory = CreateSut(physicalFactory);
        sut = factory;
        factory.GetDbContext(DataSource.Sqlite);

        var act = async () => await factory.SaveChangesAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SaveChangesAsync_ContextCreatedMidSave_IsAlsoSaved()
    {
        var physicalFactory = new Mock<IPhysicalDbContextFactory>();
        DbContextFactory? sut = null;
        var secondaryKey = new DataSourceKey(DataSource.Sqlite, "Secondary");
        var secondary = MidSaveContextCreatingDbContext.CreateInert();

        physicalFactory
            .Setup(f => f.Create(It.IsAny<DataSourceKey>()))
            .Returns<DataSourceKey>(key =>
            {
                if (key == secondaryKey)
                {
                    return secondary;
                }

                return MidSaveContextCreatingDbContext.CreateThatResolves(() => sut!.GetDbContext(secondaryKey));
            });

        await using var factory = CreateSut(physicalFactory);
        sut = factory;
        factory.GetDbContext(DataSource.Sqlite);

        await factory.SaveChangesAsync();

        secondary.SaveCount.Should().Be(1,
            "a context materialized during the save must still be saved in the same unit of work");
    }

    private static DbContextFactory CreateSut(Mock<IPhysicalDbContextFactory>? physicalFactory = null)
    {
        var registry = new Mock<IEntityDataSourceRegistry>();
        registry.Setup(r => r.GetPhysicalSourcesInUse()).Returns([]);

        return new DbContextFactory(
            (physicalFactory ?? new Mock<IPhysicalDbContextFactory>()).Object,
            registry.Object,
            Mock.Of<IDataSourceResolver>(),
            Mock.Of<ICurrentUserService>());
    }

    /// <summary>
    /// Stands in for a context whose save reaches back into the factory, the way in-process domain
    /// event dispatch does when a handler touches a data source that has not been materialized yet.
    /// The callback runs from a save interceptor, which is where that re-entrancy actually happens.
    /// </summary>
    private sealed class MidSaveContextCreatingDbContext : ApplicationDbContext
    {
        private Action? _onSave;

        private MidSaveContextCreatingDbContext(DbContextOptions<MidSaveContextCreatingDbContext> options, IServiceProvider serviceProvider)
            : base(options, serviceProvider, Mock.Of<IEntityConfigurationAssemblyProvider>(), TestPhysicalDataSources.Sqlite())
        {
        }

        public int SaveCount { get; private set; }

        public static MidSaveContextCreatingDbContext CreateInert() => Create(onSave: null);

        public static MidSaveContextCreatingDbContext CreateThatResolves(Action onSave) => Create(onSave);

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);
            optionsBuilder.AddInterceptors(new ReentrantSaveInterceptor(this));
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Deliberately empty: the save has nothing to write, only the re-entrancy matters.
        }

        private static MidSaveContextCreatingDbContext Create(Action? onSave)
        {
            var services = new ServiceCollection();
            services.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
            services.AddSingleton(new DomainEventSaveChangesInterceptor(
                Mock.Of<IDomainEventDispatcher>(),
                NullLogger<DomainEventSaveChangesInterceptor>.Instance,
                Mock.Of<IOutboxSignal>()));
            services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());

            var options = new DbContextOptionsBuilder<MidSaveContextCreatingDbContext>()
                .UseSqlite("DataSource=:memory:")
                .Options;

            var context = new MidSaveContextCreatingDbContext(options, services.BuildServiceProvider())
            {
                _onSave = onSave,
            };
            context.Database.OpenConnection();
            context.Database.EnsureCreated();
            return context;
        }

        private sealed class ReentrantSaveInterceptor(MidSaveContextCreatingDbContext owner) : SaveChangesInterceptor
        {
            public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
                DbContextEventData eventData,
                InterceptionResult<int> result,
                CancellationToken cancellationToken = default)
            {
                owner.SaveCount++;
                owner._onSave?.Invoke();
                return base.SavingChangesAsync(eventData, result, cancellationToken);
            }
        }
    }
}

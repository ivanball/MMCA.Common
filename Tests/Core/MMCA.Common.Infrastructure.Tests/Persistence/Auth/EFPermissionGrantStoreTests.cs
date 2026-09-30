using System.Reflection;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.Permissions;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.Auth;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Auth;

/// <summary>
/// The EF grant store against a real SQLite database, because the idempotent-grant promise under a
/// concurrent duplicate is the unique index's to keep (L52): the loser of the race must answer
/// success and leave nothing tracked for the next save to retry.
/// </summary>
public sealed class EFPermissionGrantStoreTests : IAsyncDisposable
{
    private const string Role = "Manager";
    private const string Permission = "sessions:read";

    private static readonly DateTime Now = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly List<ApplicationDbContext> _contexts = [];

    [Fact]
    public async Task GrantAsync_WhenAConcurrentDuplicateWinsTheInsert_AnswersSuccessAndTracksNothing()
    {
        await _connection.OpenAsync(CancellationToken.None);
        ApplicationDbContext storeContext = NewContext();
        ApplicationDbContext racerContext = NewContext();

        var dbContextFactory = new Mock<IDbContextFactory>();
        dbContextFactory.Setup(f => f.GetDbContext(It.IsAny<DataSourceKey>())).Returns(storeContext);
        dbContextFactory
            .Setup(f => f.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
            {
                // The other request inserts the same (Role, Permission) between this request's
                // existence check and its save: exactly the race the unique index settles.
                racerContext.Add(PermissionGrant.Create(Role, Permission, Now).Value!);
                await racerContext.SaveChangesAsync(ct);
                return await storeContext.SaveChangesAsync(ct);
            });

        var sut = new EFPermissionGrantStore(
            dbContextFactory.Object,
            new EmptyEntityDataSourceRegistry(),
            Mock.Of<IDataSourceResolver>(),
            Options.Create(new PermissionGrantSettings()),
            TimeProvider.System,
            new SqlServerUniqueConstraintViolationDetector());

        var result = await sut.GrantAsync(Role, Permission, cancellationToken: CancellationToken.None);

        result.IsSuccess.Should().BeTrue("the row the winner wrote says exactly what the loser meant");
        storeContext.ChangeTracker.Entries<PermissionGrant>().Should().BeEmpty(
            "the failed insert must not stay tracked for the scoped context's next save");
        (await NewContext().Set<PermissionGrant>().AsNoTracking().CountAsync(CancellationToken.None)).Should().Be(1);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (ApplicationDbContext context in _contexts)
        {
            await context.DisposeAsync();
        }

        await _connection.DisposeAsync();
    }

    private GrantTestContext NewContext()
    {
        var context = GrantTestContext.Create(_connection);
        _contexts.Add(context);
        return context;
    }

    private sealed class GrantTestContext : ApplicationDbContext
    {
        private GrantTestContext(DbContextOptions<GrantTestContext> options, IServiceProvider serviceProvider)
            : base(options, serviceProvider, new NullAssemblyProvider(), TestPhysicalDataSources.Sqlite())
        {
        }

        public static GrantTestContext Create(SqliteConnection connection)
        {
            var options = new DbContextOptionsBuilder<GrantTestContext>()
                .UseSqlite(connection)
                .Options;

            var context = new GrantTestContext(options, BuildContextServices());
            context.Database.EnsureCreated();
            return context;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.ApplyPermissionGrantConfiguration(schema: null);

        /// <summary>The minimal service graph an <c>ApplicationDbContext</c> resolves at construction.</summary>
        private static ServiceProvider BuildContextServices()
        {
            var services = new ServiceCollection();
            services.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
            services.AddSingleton(_ => new DomainEventSaveChangesInterceptor(
                Mock.Of<IDomainEventDispatcher>(),
                Mock.Of<ILogger<DomainEventSaveChangesInterceptor>>(),
                Mock.Of<IOutboxSignal>(),
                timeProvider: TimeProvider.System));
            services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
            return services.BuildServiceProvider();
        }
    }

    private sealed class NullAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<Assembly> GetConfigurationAssemblies() => [];
    }
}

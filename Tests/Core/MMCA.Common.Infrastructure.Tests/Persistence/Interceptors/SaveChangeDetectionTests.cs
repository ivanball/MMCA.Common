using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Interceptors;

/// <summary>
/// A save must run change detection exactly once. Two interceptors scan the ChangeTracker from
/// SavingChanges and EF detects again on its own, and because <c>Entries&lt;T&gt;()</c> memoizes
/// nothing, each of those scans used to pay a full O(tracked entities x properties) snapshot
/// comparison. These tests pin the count and, just as importantly, pin that suppressing the extra
/// passes did not cost the tracker any actual changes.
/// </summary>
public sealed class SaveChangeDetectionTests : IDisposable
{
    private readonly DetectionTestDbContext _dbContext = DetectionTestDbContext.Create();

    public void Dispose() => _dbContext.Dispose();

    [Fact]
    public async Task Save_DetectsChangesExactlyOnce()
    {
        _dbContext.Entities.Add(new Widget { Id = 1, Name = "First" });
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var detections = 0;
        _dbContext.ChangeTracker.DetectedAllChanges += (_, _) => detections++;

        var tracked = await _dbContext.Entities.FirstAsync(TestContext.Current.CancellationToken);
        tracked.Name = "Renamed";
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        detections.Should().Be(1, "one detection pass covers the audit scan, the domain-event scan, and EF's own");
    }

    [Fact]
    public async Task Save_StillPersistsAnUntrackedPropertyEdit()
    {
        // The behavioural half: detection is suppressed only AFTER one explicit pass, so a plain
        // property assignment made before SaveChanges must still reach the database.
        _dbContext.Entities.Add(new Widget { Id = 1, Name = "First" });
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var tracked = await _dbContext.Entities.FirstAsync(TestContext.Current.CancellationToken);
        tracked.Name = "Renamed";
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        _dbContext.ChangeTracker.Clear();
        var reloaded = await _dbContext.Entities.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken);
        reloaded.Name.Should().Be("Renamed");
    }

    [Fact]
    public async Task Save_StampsAuditFields()
    {
        // The audit interceptor writes through entry.Property(...).CurrentValue, which takes effect
        // without detection. Guards that suppression did not silently drop the stamps.
        _dbContext.Entities.Add(new Widget { Id = 7, Name = "Stamped" });
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        _dbContext.ChangeTracker.Clear();
        var reloaded = await _dbContext.Entities.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken);
        reloaded.CreatedOn.Should().NotBe(default);
        reloaded.LastModifiedOn.Should().NotBe(default);
    }

    [Fact]
    public async Task Save_RestoresTheCallersAutoDetectSetting()
    {
        _dbContext.ChangeTracker.AutoDetectChangesEnabled = false;

        _dbContext.Entities.Add(new Widget { Id = 2, Name = "Second" });
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        _dbContext.ChangeTracker.AutoDetectChangesEnabled.Should().BeFalse(
            "a caller that deliberately disabled auto-detect must not have it switched back on");
    }

    [Fact]
    public async Task Save_LeavesAutoDetectEnabledForTheNextCaller()
    {
        _dbContext.Entities.Add(new Widget { Id = 3, Name = "Third" });
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        _dbContext.ChangeTracker.AutoDetectChangesEnabled.Should().BeTrue(
            "suppression lasts for the save only, not for the context's remaining lifetime");
    }

    // ── Test doubles ──
    public sealed class Widget : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class DetectionTestDbContext : ApplicationDbContext
    {
        public DbSet<Widget> Entities => Set<Widget>();

        internal override bool SupportsOutbox => false;

        private DetectionTestDbContext(DbContextOptions<DetectionTestDbContext> options, IServiceProvider serviceProvider)
            : base(options, serviceProvider, new NullAssemblyProvider(), TestPhysicalDataSources.Sqlite())
        {
        }

        public static DetectionTestDbContext Create()
        {
            var services = new ServiceCollection();
            services.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
            services.AddSingleton(new DomainEventSaveChangesInterceptor(
                Mock.Of<IDomainEventDispatcher>(),
                NullLogger<DomainEventSaveChangesInterceptor>.Instance,
                Mock.Of<IOutboxSignal>()));
            services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
            IServiceProvider sp = services.BuildServiceProvider();

            var options = new DbContextOptionsBuilder<DetectionTestDbContext>()
                .UseSqlite("DataSource=:memory:")
                .Options;

            var context = new DetectionTestDbContext(options, sp);
            context.Database.OpenConnection();
            context.Database.EnsureCreated();
            return context;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Widget>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedNever();
                e.Property(x => x.Name);
                e.Property(x => x.RowVersion).IsConcurrencyToken();
            });
    }

    private sealed class NullAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<System.Reflection.Assembly> GetConfigurationAssemblies() => [];
    }
}

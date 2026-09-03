using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Attributes;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.AuditTrail;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.AuditTrail;

/// <summary>
/// Shared scaffolding for the audit-trail tests: an in-memory SQLite
/// <see cref="ApplicationDbContext"/> mapping an opted-in entity, an entity that never opted in,
/// and the trail table itself (the OutboxCleanupServiceTests / SchedulerTestHarness pattern).
/// The production gate that decides whether the table is in the model at all is covered separately
/// by <c>AuditTrailModelGateTests</c>, so this harness maps it unconditionally.
/// </summary>
internal static class AuditTrailTestHarness
{
    /// <summary>Epoch for every audit-trail test, passed explicitly so captured timestamps read plainly.</summary>
    public static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The <see cref="Epoch"/> as the UTC <see cref="DateTime"/> the interceptor records.</summary>
    public static DateTime EpochUtc => Epoch.UtcDateTime;

    public static FakeTimeProvider CreateTimeProvider() => new(Epoch);
}

/// <summary>An entity that opted in to the change trail, including one personal-data property.</summary>
public sealed class AuditedThing : IAuditedEntity
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    [Pii]
    public string Email { get; set; } = string.Empty;

    public int Quantity { get; set; }
}

/// <summary>An entity that never opted in; nothing it does may reach the trail.</summary>
public sealed class PlainThing
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>An opted-in entity with a composite, application-assigned key.</summary>
public sealed class CompositeKeyThing : IAuditedEntity
{
    public int PartA { get; set; }

    public string PartB { get; set; } = string.Empty;

    public string Note { get; set; } = string.Empty;
}

/// <summary>
/// A context that runs the real interceptor pipeline (audit stamps, domain events, then the change
/// trail) over SQLite, so the trail rows a test asserts on are the rows production would write.
/// </summary>
public sealed class AuditTrailTestContext : ApplicationDbContext
{
    private AuditTrailTestContext(DbContextOptions<AuditTrailTestContext> options, IServiceProvider serviceProvider)
        : base(options, serviceProvider, new NullAssemblyProvider(), TestPhysicalDataSources.Sqlite())
    {
    }

    public DbSet<AuditedThing> AuditedThings => Set<AuditedThing>();

    public DbSet<PlainThing> PlainThings => Set<PlainThing>();

    public DbSet<CompositeKeyThing> CompositeKeyThings => Set<CompositeKeyThing>();

    public DbSet<AuditTrailEntry> TrailRows => Set<AuditTrailEntry>();

    internal override bool SupportsOutbox => false;

    /// <summary>When set, the next save aborts after the trail has staged its rows.</summary>
    public bool FailNextSave { get; set; }

    /// <summary>
    /// Creates the harness context. Omitting the interceptor reproduces a host that never called
    /// <c>AddAuditTrail</c>: <c>ApplicationDbContext</c> resolves it with <c>GetService</c> and must
    /// silently record nothing.
    /// </summary>
    public static AuditTrailTestContext Create(TimeProvider timeProvider, bool registerInterceptor = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AuditSaveChangesInterceptor(timeProvider));
        services.AddSingleton(_ =>
        {
            var dispatcher = new Mock<IDomainEventDispatcher>();
            var logger = new Mock<ILogger<DomainEventSaveChangesInterceptor>>();
            var outboxSignal = new Mock<IOutboxSignal>();
            return new DomainEventSaveChangesInterceptor(dispatcher.Object, logger.Object, outboxSignal.Object);
        });
        services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());

        if (registerInterceptor)
        {
            services.AddSingleton(new AuditTrailSaveChangesInterceptor(timeProvider));
        }

        var options = new DbContextOptionsBuilder<AuditTrailTestContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        var context = new AuditTrailTestContext(options, services.BuildServiceProvider());
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // base registers the audit, domain-event and trail interceptors; appending after it means
        // this one runs last, so the trail rows are already staged when it throws.
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.AddInterceptors(new FailingSaveInterceptor(this));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<AuditedThing>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
        });

        modelBuilder.Entity<PlainThing>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<CompositeKeyThing>(entity => entity.HasKey(e => new { e.PartA, e.PartB }));

        // SQLite-portable copy of the production mapping: no schema, no filtered indexes.
        modelBuilder.Entity<AuditTrailEntry>(entity =>
        {
            entity.ToTable("AuditTrailEntries");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EntityType).IsRequired().HasMaxLength(256);
            entity.Property(e => e.EntityKey).IsRequired().HasMaxLength(128);
            entity.Property(e => e.PropertyName).HasMaxLength(128);
            entity.Property(e => e.Operation).IsRequired().HasMaxLength(16);
            entity.Property(e => e.CorrelationId).HasMaxLength(64);
            entity.Property(e => e.TenantId).HasMaxLength(64);
            entity.HasIndex(e => new { e.EntityType, e.EntityKey, e.ChangedOn });
        });
    }

    /// <summary>
    /// Aborts the save after the trail interceptor has staged its rows, reproducing the state an
    /// execution-strategy retry starts from: rows tracked as Added and <c>SavedChanges</c> never
    /// reached.
    /// </summary>
    private sealed class FailingSaveInterceptor(AuditTrailTestContext owner) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (owner.FailNextSave)
                throw new DbUpdateException("Simulated transient save failure.");

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class NullAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<Assembly> GetConfigurationAssemblies() => [];
    }
}

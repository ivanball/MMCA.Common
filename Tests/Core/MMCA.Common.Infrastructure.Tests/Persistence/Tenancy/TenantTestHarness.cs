using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.AuditTrail;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Tenancy;

/// <summary>A tenant-owned, soft-deletable entity: the shape both global filters apply to.</summary>
public sealed class TenantThing : AuditableBaseEntity<int>, ITenantEntity
{
    /// <summary>Gets or sets the owning tenant. Public setter for test seeding only.</summary>
    public string TenantId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>An owned value that also carries the marker, to prove owned types are excluded.</summary>
    public TenantDetail Detail { get; set; } = new();
}

/// <summary>An owned type carrying the marker; it must inherit its owner's filter, not get its own.</summary>
public sealed class TenantDetail : ITenantEntity
{
    /// <summary>Gets or sets the owning tenant. Public setter for test seeding only.</summary>
    public string TenantId { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;
}

/// <summary>An entity that never opted in to tenancy; nothing tenant-related may touch it.</summary>
public sealed class PlainThing : AuditableBaseEntity<int>
{
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// A tenant-owned entity that is NOT soft-deletable: its reads carry the tenant predicate alone, so
/// it must keep the single-column tenant index rather than the composite one.
/// </summary>
public sealed class TenantOnlyThing : BaseEntity<int>, ITenantEntity
{
    /// <summary>Gets or sets the owning tenant. Public setter for test seeding only.</summary>
    public string TenantId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

/// <summary>An opted-in entity that is also change-trailed, for the trail's TenantId column.</summary>
public sealed class TrailedTenantThing : AuditableBaseEntity<int>, ITenantEntity, IAuditedEntity
{
    /// <summary>Gets or sets the owning tenant. Public setter for test seeding only.</summary>
    public string TenantId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// A context running the real interceptor pipeline and the real named query filters over SQLite, so
/// the rows a test sees are the rows production would see (the AuditTrailTestHarness pattern).
/// </summary>
public sealed class TenantTestContext : ApplicationDbContext
{
    private TenantTestContext(DbContextOptions<TenantTestContext> options, IServiceProvider serviceProvider)
        : base(options, serviceProvider, new NullAssemblyProvider(), TestPhysicalDataSources.Sqlite())
    {
    }

    public DbSet<TenantThing> Things => Set<TenantThing>();

    public DbSet<PlainThing> PlainThings => Set<PlainThing>();

    public DbSet<TrailedTenantThing> TrailedThings => Set<TrailedTenantThing>();

    public DbSet<AuditTrailEntry> TrailRows => Set<AuditTrailEntry>();

    internal override bool SupportsOutbox => false;

    /// <summary>
    /// Creates a context over <paramref name="connection"/> whose tenant is whatever
    /// <paramref name="tenantAccessor"/> answers at the moment a query or save runs.
    /// </summary>
    /// <param name="connection">An already-open SQLite connection; share it to share a database.</param>
    /// <param name="tenantAccessor">The live tenant accessor, or null for a system context.</param>
    /// <param name="registerTenantInterceptor">
    /// False reproduces a container that never registered the tenant interceptor, which must still
    /// build a context (design time, a directly-constructed test double).
    /// </param>
    /// <param name="registerTrailInterceptor">Whether the change trail records this context's saves.</param>
    /// <returns>The context; the caller owns disposal.</returns>
    public static TenantTestContext Create(
        SqliteConnection connection,
        Func<string?>? tenantAccessor = null,
        bool registerTenantInterceptor = true,
        bool registerTrailInterceptor = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
        services.AddSingleton(new DomainEventSaveChangesInterceptor(
            Mock.Of<IDomainEventDispatcher>(),
            NullLogger<DomainEventSaveChangesInterceptor>.Instance,
            Mock.Of<IOutboxSignal>()));
        services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());

        if (registerTenantInterceptor)
        {
            services.AddSingleton(new TenantSaveChangesInterceptor());
        }

        if (registerTrailInterceptor)
        {
            services.AddSingleton(new AuditTrailSaveChangesInterceptor(TimeProvider.System));
        }

        var options = new DbContextOptionsBuilder<TenantTestContext>()
            .UseSqlite(connection)
            .Options;

        var context = new TenantTestContext(options, services.BuildServiceProvider())
        {
            TenantIdAccessor = tenantAccessor,
        };

        return context;
    }

    /// <summary>Opens a fresh in-memory SQLite connection and creates the schema once.</summary>
    /// <returns>The open connection; the caller owns disposal.</returns>
    public static SqliteConnection OpenDatabase()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        using var creator = Create(connection);
        creator.Database.EnsureCreated();

        return connection;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<TenantThing>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.RowVersion).IsConcurrencyToken();
            entity.OwnsOne(e => e.Detail);
        });

        modelBuilder.Entity<PlainThing>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.RowVersion).IsConcurrencyToken();
        });

        modelBuilder.Entity<TenantOnlyThing>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<TrailedTenantThing>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.RowVersion).IsConcurrencyToken();
        });

        // SQLite-portable copy of the production trail mapping (no schema, no filtered indexes).
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
        });

        // The behavior under test: both named global filters, applied exactly as production does.
        ApplySoftDeleteFilters(modelBuilder);
        ApplyTenantFilters(modelBuilder);
    }

    private sealed class NullAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<Assembly> GetConfigurationAssemblies() => [];
    }
}

using Microsoft.EntityFrameworkCore;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.DbContexts;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Specifications;

/// <summary>
/// A parent aggregate with a scalar sort key, a nullable sort key, and a child collection, used by
/// the specification and keyset paging tests to exercise ordering, includes, and paging against a
/// real provider.
/// </summary>
public sealed class SpecTestEntity : AuditableBaseEntity<int>
{
    /// <summary>Gets or sets the non-unique display name (a deliberately duplicated sort key).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the numeric rank.</summary>
    public int Rank { get; set; }

    /// <summary>Gets or sets the optional category (a nullable sort key).</summary>
    public string? Category { get; set; }

    /// <summary>Gets or sets the child collection, so includes can be exercised.</summary>
    public ICollection<SpecTestChild> Children { get; set; } = [];
}

/// <summary>The child of <see cref="SpecTestEntity"/>, reached through a collection navigation.</summary>
public sealed class SpecTestChild : AuditableBaseEntity<int>
{
    /// <summary>Gets or sets the owning parent's identifier.</summary>
    public int SpecTestEntityId { get; set; }

    /// <summary>Gets or sets the child label.</summary>
    public string Label { get; set; } = string.Empty;
}

/// <summary>
/// A minimal SQLite-mappable context over the specification test entities. The production
/// configurations are SQL Server specific, so the mapping is declared here, exactly as the
/// neighbouring EF repository integration tests do. The soft-delete filter is registered under the
/// production NAME, because the repository drops that filter by name.
/// </summary>
public sealed class SpecificationTestDbContext(DbContextOptions options) : DbContext(options)
{
    /// <summary>Gets the parent set.</summary>
    public DbSet<SpecTestEntity> Entities => Set<SpecTestEntity>();

    /// <summary>Gets the child set.</summary>
    public DbSet<SpecTestChild> Children => Set<SpecTestChild>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SpecTestEntity>(b =>
        {
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).ValueGeneratedNever();
            b.Property(e => e.Name);
            b.Property(e => e.Rank);
            b.Property(e => e.Category);
            b.Ignore(e => e.RowVersion);
            b.HasMany(e => e.Children).WithOne().HasForeignKey(c => c.SpecTestEntityId);
            b.HasQueryFilter(ApplicationDbContext.SoftDeleteFilterName, e => !e.IsDeleted);
        });

        modelBuilder.Entity<SpecTestChild>(b =>
        {
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).ValueGeneratedNever();
            b.Property(e => e.Label);
            b.Ignore(e => e.RowVersion);
            b.HasQueryFilter(ApplicationDbContext.SoftDeleteFilterName, e => !e.IsDeleted);
        });
    }
}

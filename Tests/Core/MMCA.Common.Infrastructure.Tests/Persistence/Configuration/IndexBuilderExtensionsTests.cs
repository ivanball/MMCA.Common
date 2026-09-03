using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.Configuration;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Configuration;

/// <summary>
/// Tests for <c>IndexBuilderExtensions.HasSoftDeleteFilter</c>: the opt-in replacement for the
/// literal <c>HasFilter("[IsDeleted] = 0")</c> on hand-authored NON-unique indexes (the unique ones
/// are already covered automatically by <c>SoftDeleteUniqueIndexConvention</c>). It must produce the
/// same SQL the literal produced, and it must derive quoting from the engine using the very same
/// predicate builder the convention uses.
/// </summary>
public sealed class IndexBuilderExtensionsTests : IDisposable
{
    private readonly FilteredIndexTestDbContext _dbContext = FilteredIndexTestDbContext.Create();

    public void Dispose() => _dbContext.Dispose();

    [Fact]
    public void HasSoftDeleteFilter_DefaultsToSqlServerBracketQuoting()
        => Filter<SqlServerIndexedEntity>(nameof(SqlServerIndexedEntity.Name))
            .Should().Be("[IsDeleted] = 0", "this is the literal the 17 hand-authored call sites carry today");

    [Fact]
    public void HasSoftDeleteFilter_OnSqlite_UsesDoubleQuoteQuoting()
        => Filter<SqliteIndexedEntity>(nameof(SqliteIndexedEntity.Name))
            .Should().Be("\"IsDeleted\" = 0", "same branch SoftDeleteUniqueIndexConvention takes for SQLite");

    [Fact]
    public void HasSoftDeleteFilter_OnCosmos_LeavesTheIndexUnfiltered()
        => Filter<CosmosIndexedEntity>(nameof(CosmosIndexedEntity.Name))
            .Should().BeNull("Cosmos has no filtered indexes, exactly as the convention skips it");

    [Fact]
    public void HasSoftDeleteFilter_WithAnAdditionalFilter_CombinesInTheHandAuthoredOrder()
        => Filter<SqlServerIndexedEntity>(nameof(SqlServerIndexedEntity.Sku))
            .Should().Be(
                "[Sku] IS NOT NULL AND [IsDeleted] = 0",
                "the combined literal it replaces reads predicate first, soft-delete second");

    [Fact]
    public void HasSoftDeleteFilter_StaysChainable()
    {
        var index = _dbContext.Model.FindEntityType(typeof(SqlServerIndexedEntity))!
            .GetIndexes()
            .Single(i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(SqlServerIndexedEntity.Code));

        index.GetDatabaseName().Should().Be("IX_Chained");
        index.GetFilter().Should().Be("[IsDeleted] = 0");
    }

    [Fact]
    public void HasSoftDeleteFilter_FollowsARenamedIsDeletedColumn()
        => Filter<RenamedFlagEntity>(nameof(RenamedFlagEntity.Name))
            .Should().Be("[Deleted] = 0", "the column name comes from the model, not from a string literal");

    private string? Filter<TEntity>(string propertyName)
        => _dbContext.Model.FindEntityType(typeof(TEntity))!
            .GetIndexes()
            .Single(i => i.Properties.Count == 1 && i.Properties[0].Name == propertyName)
            .GetFilter();

    // ── Test doubles ──
    public sealed class SqlServerIndexedEntity : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;

        public string Code { get; set; } = string.Empty;

        public string? Sku { get; set; }
    }

    public sealed class SqliteIndexedEntity : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class CosmosIndexedEntity : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class RenamedFlagEntity : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class FilteredIndexTestDbContext : DbContext
    {
        private FilteredIndexTestDbContext(DbContextOptions<FilteredIndexTestDbContext> options)
            : base(options)
        {
        }

        public static FilteredIndexTestDbContext Create()
        {
            var options = new DbContextOptionsBuilder<FilteredIndexTestDbContext>()
                .UseSqlite("DataSource=:memory:")
                .Options;

            return new FilteredIndexTestDbContext(options);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SqlServerIndexedEntity>(builder =>
            {
                builder.HasKey(p => p.Id);
                builder.HasIndex(p => p.Name).HasSoftDeleteFilter();
                builder.HasIndex(p => p.Sku).HasSoftDeleteFilter(additionalFilter: "[Sku] IS NOT NULL");
                builder.HasIndex(p => p.Code).HasSoftDeleteFilter().HasDatabaseName("IX_Chained");
            });

            modelBuilder.Entity<SqliteIndexedEntity>(builder =>
            {
                builder.HasKey(p => p.Id);
                builder.HasIndex(p => p.Name).HasSoftDeleteFilter(DataSource.Sqlite);
            });

            modelBuilder.Entity<CosmosIndexedEntity>(builder =>
            {
                builder.HasKey(p => p.Id);
                builder.HasIndex(p => p.Name).HasSoftDeleteFilter(DataSource.CosmosDB);
            });

            modelBuilder.Entity<RenamedFlagEntity>(builder =>
            {
                builder.HasKey(p => p.Id);

                // The column name is read when the extension is called, so the rename has to precede it.
                builder.Property(p => p.IsDeleted).HasColumnName("Deleted");
                builder.HasIndex(p => p.Name).HasSoftDeleteFilter();
            });
        }
    }
}

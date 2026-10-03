using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MMCA.Common.Infrastructure.Persistence.DataSources.Engines;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;

namespace MMCA.Common.Infrastructure.Tests.Persistence.DbContexts.Factory;

/// <summary>
/// The save order of explicit-key insert rounds (O-09): one round per table, and a round hides every
/// other round's added rows, so a dependent table saved before its principal violates the foreign key
/// (FK_CategoryItem_Category_CategoryId on a Sessionize import into a fresh event). The order must
/// come from the model's foreign keys, not from the order rows were added.
/// </summary>
public sealed class ExplicitKeyInsertRoundOrderTests
{
    [Fact]
    public void Order_WithTheDependentRoundFirst_PutsThePrincipalRoundFirst()
    {
        using var context = new CatalogContext();
        IReadOnlyEntityType category = context.Model.FindEntityType(typeof(Category))!;
        IReadOnlyEntityType categoryItem = context.Model.FindEntityType(typeof(CategoryItem))!;

        var order = ExplicitKeyInsertRoundOrder.Order([[categoryItem], [category]]);

        order.Should().Equal([1, 0], "Category rows must exist before CategoryItem rows reference them");
    }

    [Fact]
    public void Order_ARealChangeTrackerAddedDependentFirst_SavesThePrincipalTableFirst()
    {
        // The import adds a category item before its category: FindGroups groups by first appearance,
        // so the dependent table comes back first and must be reordered.
        using var context = new CatalogContext();
        context.Add(new CategoryItem { Id = 20, CategoryId = 10, Name = "Track A" });
        context.Add(new Category { Id = 10, Name = "Track" });

        var groups = new SQLServerDataSourceEngine().FindGroups(context);
        groups.Select(g => g.Table).Should().Equal(nameof(CategoryItem), nameof(Category));

        var order = ExplicitKeyInsertRoundOrder.Order(
            [.. groups.Select(g => (IReadOnlyCollection<IReadOnlyEntityType>)[.. g.Entries.Select(e => e.Metadata).Distinct()])]);

        order.Select(i => groups[i].Table).Should().Equal(nameof(Category), nameof(CategoryItem));
    }

    [Fact]
    public void Order_WithIndependentRounds_KeepsTheirOriginalOrder()
    {
        using var context = new CatalogContext();
        IReadOnlyEntityType category = context.Model.FindEntityType(typeof(Category))!;
        IReadOnlyEntityType tag = context.Model.FindEntityType(typeof(Tag))!;

        ExplicitKeyInsertRoundOrder.Order([[tag], [category]]).Should().Equal(0, 1);
        ExplicitKeyInsertRoundOrder.Order([[category], [tag]]).Should().Equal(0, 1);
    }

    [Fact]
    public void Order_WithASelfReferencingTable_TreatsItAsNoDependency()
    {
        using var context = new CatalogContext();
        IReadOnlyEntityType tag = context.Model.FindEntityType(typeof(Tag))!;
        IReadOnlyEntityType category = context.Model.FindEntityType(typeof(Category))!;

        // Tag.ParentTagId points at Tag itself; EF orders those rows inside the one round.
        ExplicitKeyInsertRoundOrder.Order([[tag], [category]]).Should().Equal(0, 1);
    }

    [Fact]
    public void Order_WithACycleBetweenRounds_ReturnsEveryRoundOnceInAStableOrder()
    {
        using var context = new CatalogContext();
        IReadOnlyEntityType left = context.Model.FindEntityType(typeof(CycleLeft))!;
        IReadOnlyEntityType right = context.Model.FindEntityType(typeof(CycleRight))!;
        IReadOnlyEntityType category = context.Model.FindEntityType(typeof(Category))!;
        IReadOnlyEntityType categoryItem = context.Model.FindEntityType(typeof(CategoryItem))!;

        var first = ExplicitKeyInsertRoundOrder.Order([[categoryItem], [right], [left], [category]]).ToList();
        var second = ExplicitKeyInsertRoundOrder.Order([[categoryItem], [right], [left], [category]]).ToList();

        first.Should().BeEquivalentTo([0, 1, 2, 3]);
        first.Should().Equal(second, "the cycle is broken the same way every time");
        first.IndexOf(3).Should().BeLessThan(first.IndexOf(0), "the acyclic pair is still ordered principal first");
    }

    private sealed class Category
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class CategoryItem
    {
        public int Id { get; set; }

        public int CategoryId { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed record Tag(int Id, int? ParentTagId);

    private sealed record CycleLeft(int Id, int? RightId);

    private sealed record CycleRight(int Id, int? LeftId);

    private sealed class CatalogContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder.UseSqlServer("Server=(local);Database=model-only;Trusted_Connection=True;");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Category>().ToTable(nameof(Category), "Conference");
            modelBuilder.Entity<CategoryItem>().ToTable(nameof(CategoryItem), "Conference")
                .HasOne<Category>().WithMany().HasForeignKey(i => i.CategoryId);
            modelBuilder.Entity<Tag>().ToTable(nameof(Tag), "Conference")
                .HasOne<Tag>().WithMany().HasForeignKey(t => t.ParentTagId);
            modelBuilder.Entity<CycleLeft>().ToTable(nameof(CycleLeft), "Conference")
                .HasOne<CycleRight>().WithMany().HasForeignKey(l => l.RightId);
            modelBuilder.Entity<CycleRight>().ToTable(nameof(CycleRight), "Conference")
                .HasOne<CycleLeft>().WithMany().HasForeignKey(r => r.LeftId);
        }
    }
}

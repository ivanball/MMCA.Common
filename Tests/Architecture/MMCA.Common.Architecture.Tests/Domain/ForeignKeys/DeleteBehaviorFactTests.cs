using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MMCA.Common.Architecture.Tests.ForeignKeyFixtures;
using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Domain.ForeignKeys;

/// <summary>
/// Adversarial coverage for the two delete-behavior rules and the model walk beneath them
/// (<c>ForeignKeyDeleteFacts</c>, <c>AnnotationValue</c>). <c>DeleteBehaviorConventionTests</c> runs the
/// rules over the framework's own models, which hold no foreign keys today, so the walk never reaches
/// a key there. Here a finalized EF model built from the Sqlite conventions carries real
/// relationships (inherited cascade, explicit cascade, convention restrict, a composite key and an
/// ownership), and duck-typed metadata objects pin the fallbacks and the not-an-EF-model failures.
/// </summary>
public sealed class DeleteBehaviorFactTests
{
    private const string SourceAnnotation = FakeForeignKey.SourceAnnotation;

    // -- CascadingForeignKeysAreExplicitlyOptedIn --
    [Fact]
    public void InheritedCascade_IsFlagged_WithItsColumnsAndAbsentSource()
    {
        var model = Model(builder => builder.Entity<FkChild>()
            .HasOne(c => c.Parent).WithMany().HasForeignKey(c => c.ParentId).IsRequired());

        var act = () => ArchitectureRules.CascadingForeignKeysAreExplicitlyOptedIn(model);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain(
                $"{typeof(FkChild).FullName}(ParentId) deletes with Cascade, but its delete-behavior source is absent rather than Explicit",
                "EF's default for a required relationship is a cascade nobody chose");
    }

    [Fact]
    public void ExplicitCascade_Passes()
    {
        var model = Model(builder => builder.Entity<FkChild>()
            .HasOne(c => c.Parent).WithMany().HasForeignKey(c => c.ParentId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasAnnotation(SourceAnnotation, "Explicit"));

        var act = () => ArchitectureRules.CascadingForeignKeysAreExplicitlyOptedIn(model);

        act.Should().NotThrow("a cascade the configuration opted into is a decision, not an accident");
    }

    [Fact]
    public void ClientCascade_FromTheConvention_IsFlagged_NamingTheSource()
    {
        var model = Model(builder => builder.Entity<FkChild>()
            .HasOne(c => c.Parent).WithMany().HasForeignKey(c => c.ParentId)
            .OnDelete(DeleteBehavior.ClientCascade)
            .HasAnnotation(SourceAnnotation, "Convention"));

        var act = () => ArchitectureRules.CascadingForeignKeysAreExplicitlyOptedIn(model);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain("deletes with ClientCascade, but its delete-behavior source is Convention rather than Explicit");
    }

    [Fact]
    public void CompositeKey_ListsEveryColumn()
    {
        var model = Model(builder =>
        {
            builder.Entity<FkVersionedParent>().HasKey(p => new { p.Id, p.Version });
            builder.Entity<FkVersionedChild>()
                .HasOne(c => c.Parent).WithMany()
                .HasForeignKey(c => new { c.ParentId, c.ParentVersion })
                .IsRequired();
        });

        var act = () => ArchitectureRules.CascadingForeignKeysAreExplicitlyOptedIn(model);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain($"{typeof(FkVersionedChild).FullName}(ParentId, ParentVersion) deletes with Cascade");
    }

    [Fact]
    public void RestrictAndOwnership_AreNotCascadesTheRuleGoverns()
    {
        var model = Model(builder =>
        {
            builder.Entity<FkChild>()
                .HasOne(c => c.Parent).WithMany().HasForeignKey(c => c.ParentId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.Entity<FkOwner>().OwnsOne(o => o.Address);
        });

        var act = () => ArchitectureRules.CascadingForeignKeysAreExplicitlyOptedIn(model);

        act.Should().NotThrow("a restrict is not a cascade, and an ownership's cascade is EF's, not a choice");
    }

    // -- ConventionForeignKeysRestrictDeletes --
    [Fact]
    public void ConventionKeyThatDoesNotRestrict_IsFlagged()
    {
        var model = Model(builder => builder.Entity<FkChild>()
            .HasOne(c => c.Parent).WithMany().HasForeignKey(c => c.ParentId)
            .OnDelete(DeleteBehavior.SetNull)
            .HasAnnotation(SourceAnnotation, "Convention"));

        var act = () => ArchitectureRules.ConventionForeignKeysRestrictDeletes(model);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain(
                $"{typeof(FkChild).FullName}(ParentId) was left to the convention but deletes with SetNull, not Restrict");
    }

    [Fact]
    public void ConventionKeyThatRestricts_AndExplicitOrUnstampedKeys_Pass()
    {
        var model = Model(builder =>
        {
            builder.Entity<FkChild>()
                .HasOne(c => c.Parent).WithMany().HasForeignKey(c => c.ParentId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasAnnotation(SourceAnnotation, "Convention");
            builder.Entity<FkOtherChild>()
                .HasOne(c => c.Parent).WithMany().HasForeignKey(c => c.ParentId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasAnnotation(SourceAnnotation, "Explicit");
            builder.Entity<FkRequiredChild>()
                .HasOne(c => c.Parent).WithMany().HasForeignKey(c => c.ParentId)
                .IsRequired();
        });

        var act = () => ArchitectureRules.ConventionForeignKeysRestrictDeletes(model);

        act.Should().NotThrow("only keys the convention stamped are governed, and a stamped key that restricts is the safe default");
    }

    // -- The walk's fallbacks, through duck-typed metadata --
    [Fact]
    public void MissingNames_FallBackToTypeNameAndPlaceholders()
    {
        var model = new FakeModel(new FakeEntityType(
            name: null,
            new FakeForeignKey([new FakeProperty(null), new FakeProperty("Code")], deleteBehavior: null, isOwnership: null, new FakeAnnotation("Convention"))));

        var act = () => ArchitectureRules.ConventionForeignKeysRestrictDeletes(model);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain(
                $"{nameof(FakeEntityType)}(?, Code) was left to the convention but deletes with unknown, not Restrict",
                "an entity without a name, a column without a name and a key without a behavior still produce a readable line");
    }

    [Fact]
    public void AnnotationWithoutValue_AndKeyWithoutColumns_ReadAsAbsent()
    {
        var model = new FakeModel(new FakeEntityType(
            "Ledger",
            new FakeForeignKey(properties: null, "Cascade", isOwnership: false, new FakeAnnotation(null))));

        var act = () => ArchitectureRules.CascadingForeignKeysAreExplicitlyOptedIn(model);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain("Ledger() deletes with Cascade, but its delete-behavior source is absent rather than Explicit");
    }

    [Fact]
    public void OwnershipFlag_ExemptsTheCascade()
    {
        var model = new FakeModel(new FakeEntityType(
            "Ledger",
            new FakeForeignKey([new FakeProperty("OwnerId")], "Cascade", isOwnership: true, annotation: null)));

        var act = () => ArchitectureRules.CascadingForeignKeysAreExplicitlyOptedIn(model);

        act.Should().NotThrow("an ownership key is skipped whatever its delete behavior");
    }

    [Fact]
    public void ModelWithoutEntityTypes_FailsNamingTheMissingMember()
    {
        var act = () => ArchitectureRules.CascadingForeignKeysAreExplicitlyOptedIn(new object());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Object exposes no GetEntityTypes(): *not an EF Core model.");
    }

    [Fact]
    public void ForeignKeyWithoutFindAnnotation_FailsNamingTheMissingMember()
    {
        var model = new FakeModel(new FakeEntityType("Ledger", new BareForeignKey()));

        var act = () => ArchitectureRules.ConventionForeignKeysRestrictDeletes(model);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"{nameof(BareForeignKey)} exposes no FindAnnotation(string): *not an EF Core model.");
    }

    [Fact]
    public void EntityTypeWithoutName_FailsNamingTheMissingMember()
    {
        var model = new FakeModel(new NamelessEntityType());

        var act = () => ArchitectureRules.ConventionForeignKeysRestrictDeletes(model);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"{nameof(NamelessEntityType)} exposes no Name: *not an EF Core model.");
    }

    /// <summary>
    /// Finalizes a model through a Sqlite context, so relationship defaults (cascade for a required
    /// key) are EF's real ones. Only the model is read: the connection is never opened.
    /// </summary>
    private static object Model(Action<ModelBuilder> configure)
    {
        var options = new DbContextOptionsBuilder<ModelOnlyContext>()
            .UseSqlite("DataSource=:memory:")
            .ReplaceService<IModelCacheKeyFactory, PerInstanceModelCacheKeyFactory>()
            .Options;

        using var context = new ModelOnlyContext(options, configure);
        return context.Model;
    }

    /// <summary>A context whose model is whatever the test configures.</summary>
    private sealed class ModelOnlyContext(DbContextOptions<ModelOnlyContext> options, Action<ModelBuilder> configure)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<FkParent>();
            configure(modelBuilder);
        }
    }

    /// <summary>Gives every context its own model, since each test configures a different one.</summary>
    private sealed class PerInstanceModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => context;
    }
}

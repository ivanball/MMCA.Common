using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for <see cref="EntityTypeConfigurationBase{TEntity,TIdentifierType}"/> to verify
/// that Configure excludes DomainEvents for aggregate roots. (The entity-to-data-source
/// registration that used to happen here moved to the eagerly-built EntityDataSourceRegistry.)
/// </summary>
public sealed class EntityTypeConfigurationBaseTests
{
    // ── Configure for aggregate root entity: excludes DomainEvents ──
    [Fact]
    public void Configure_AggregateRootEntity_ExcludesDomainEvents()
    {
        var config = new TestAggregateEntityConfiguration();

        var options = new DbContextOptionsBuilder()
            .UseSqlite("DataSource=:memory:")
            .Options;

        using var context = new TestConfigDbContext(options, config);

        // The entity should be in the model
        var entityType = context.Model.FindEntityType(typeof(TestAggregateEntity));
        entityType.Should().NotBeNull();

        // DomainEvents should be excluded (ignored) from the model
        var domainEventsProperty = entityType!.FindProperty("DomainEvents");
        domainEventsProperty.Should().BeNull("DomainEvents should be ignored for aggregate root entities");
    }

    [Fact]
    public void Configure_NonAggregateEntity_MapsEntity()
    {
        var config = new TestNonAggregateEntityConfiguration();

        var options = new DbContextOptionsBuilder()
            .UseSqlite("DataSource=:memory:")
            .Options;

        using var context = new TestNonAggregateConfigDbContext(options, config);

        // The entity should be in the model
        var entityType = context.Model.FindEntityType(typeof(TestNonAggregateEntity));
        entityType.Should().NotBeNull();
    }

    public sealed class TestAggregateEntity : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class TestNonAggregateEntity : AuditableBaseEntity<int>
    {
        public string Value { get; set; } = string.Empty;
    }

    public sealed class TestAggregateEntityConfiguration
        : EntityTypeConfigurationBase<TestAggregateEntity, int>
    {
        public override void Configure(EntityTypeBuilder<TestAggregateEntity> builder)
        {
            base.Configure(builder);
            builder.HasKey(e => e.Id);
            builder.Property(e => e.Id).ValueGeneratedNever();
            builder.Property(e => e.Name).HasMaxLength(100);
        }
    }

    public sealed class TestNonAggregateEntityConfiguration
        : EntityTypeConfigurationBase<TestNonAggregateEntity, int>
    {
        public override void Configure(EntityTypeBuilder<TestNonAggregateEntity> builder)
        {
            base.Configure(builder);
            builder.HasKey(e => e.Id);
            builder.Property(e => e.Id).ValueGeneratedNever();
            builder.Property(e => e.Value).HasMaxLength(200);
        }
    }

    private sealed class TestConfigDbContext(
        DbContextOptions options,
        TestAggregateEntityConfiguration config) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            config.Configure(modelBuilder.Entity<TestAggregateEntity>());
    }

    private sealed class TestNonAggregateConfigDbContext(
        DbContextOptions options,
        TestNonAggregateEntityConfiguration config) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            config.Configure(modelBuilder.Entity<TestNonAggregateEntity>());
    }
}

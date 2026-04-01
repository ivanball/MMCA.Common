using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for <see cref="EntityTypeConfigurationBase{TEntity,TIdentifierType}"/> to verify
/// that Configure registers data source and excludes DomainEvents for aggregate roots.
/// </summary>
public sealed class EntityTypeConfigurationBaseTests
{
    // ── Configure for aggregate root entity: excludes DomainEvents ──
    [Fact]
    public void Configure_AggregateRootEntity_ExcludesDomainEventsAndRegistersDataSource()
    {
        var mockDataSourceService = new Mock<IDataSourceService>();
        mockDataSourceService
            .Setup(d => d.GetDataSource(typeof(TestAggregateEntity), It.IsAny<Type>()))
            .Returns(DataSource.SQLServer);

        var config = new TestAggregateEntityConfiguration(mockDataSourceService.Object);

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

        // DataSourceService.GetDataSource was called to register the mapping
        mockDataSourceService.Verify(
            d => d.GetDataSource(typeof(TestAggregateEntity), typeof(TestAggregateEntityConfiguration)),
            Times.Once);
    }

    [Fact]
    public void Configure_NonAggregateEntity_RegistersDataSourceWithoutIgnoringDomainEvents()
    {
        var mockDataSourceService = new Mock<IDataSourceService>();
        mockDataSourceService
            .Setup(d => d.GetDataSource(typeof(TestNonAggregateEntity), It.IsAny<Type>()))
            .Returns(DataSource.Sqlite);

        var config = new TestNonAggregateEntityConfiguration(mockDataSourceService.Object);

        var options = new DbContextOptionsBuilder()
            .UseSqlite("DataSource=:memory:")
            .Options;

        using var context = new TestNonAggregateConfigDbContext(options, config);

        // The entity should be in the model
        var entityType = context.Model.FindEntityType(typeof(TestNonAggregateEntity));
        entityType.Should().NotBeNull();

        // DataSourceService.GetDataSource was called
        mockDataSourceService.Verify(
            d => d.GetDataSource(typeof(TestNonAggregateEntity), typeof(TestNonAggregateEntityConfiguration)),
            Times.Once);
    }

    public sealed class TestAggregateEntity : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class TestNonAggregateEntity : AuditableBaseEntity<int>
    {
        public string Value { get; set; } = string.Empty;
    }

    public sealed class TestAggregateEntityConfiguration(IDataSourceService dataSourceService)
        : EntityTypeConfigurationBase<TestAggregateEntity, int>(dataSourceService)
    {
        public override void Configure(EntityTypeBuilder<TestAggregateEntity> builder)
        {
            base.Configure(builder);
            builder.HasKey(e => e.Id);
            builder.Property(e => e.Id).ValueGeneratedNever();
            builder.Property(e => e.Name).HasMaxLength(100);
        }
    }

    public sealed class TestNonAggregateEntityConfiguration(IDataSourceService dataSourceService)
        : EntityTypeConfigurationBase<TestNonAggregateEntity, int>(dataSourceService)
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

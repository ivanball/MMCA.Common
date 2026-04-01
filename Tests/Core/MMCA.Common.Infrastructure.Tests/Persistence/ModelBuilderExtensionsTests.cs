using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;
using MMCA.Common.Infrastructure.Persistence.DbContexts;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

public sealed class ModelBuilderExtensionsTests
{
    [Fact]
    public void ApplyAllConfigurations_DiscoversAndAppliesConfigurationsFromAssembly()
    {
        var options = new DbContextOptionsBuilder<TestModelBuilderDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        using var context = new TestModelBuilderDbContext(options);

        // The TestModelBuilderDbContext.OnModelCreating applies configurations from this assembly
        // using the test interface ISqliteTestConfiguration<,>. If it discovers our
        // TestEntitySqliteConfiguration, the entity will be mapped.
        var model = context.Model;

        // Assert: The entity was discovered and mapped via ApplyAllConfigurations
        var entityType = model.FindEntityType(typeof(TestMappedEntity));
        entityType.Should().NotBeNull("ApplyAllConfigurations should discover TestEntitySqliteConfiguration");
    }

    // ── Null guard tests ──
    [Fact]
    public void ApplyAllConfigurations_NullAssembly_ThrowsArgumentNullException()
    {
        var modelBuilder = new ModelBuilder();
        var sp = new ServiceCollection().BuildServiceProvider();

        var act = () => ModelBuilderExtensions.ApplyAllConfigurations(
            modelBuilder, sp, null!, typeof(IEntityTypeConfigurationSqlite<,>));

        act.Should().Throw<ArgumentNullException>().WithParameterName("assembly");
    }

    [Fact]
    public void ApplyAllConfigurations_NullInterfaceType_ThrowsArgumentNullException()
    {
        var modelBuilder = new ModelBuilder();
        var sp = new ServiceCollection().BuildServiceProvider();
        var assembly = typeof(ModelBuilderExtensionsTests).Assembly;

        var act = () => ModelBuilderExtensions.ApplyAllConfigurations(
            modelBuilder, sp, assembly, null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("interfaceType");
    }

    [Fact]
    public void ApplyAllConfigurations_NullServiceProvider_ThrowsArgumentNullException()
    {
        var modelBuilder = new ModelBuilder();
        var assembly = typeof(ModelBuilderExtensionsTests).Assembly;

        var act = () => ModelBuilderExtensions.ApplyAllConfigurations(
            modelBuilder, null!, assembly, typeof(IEntityTypeConfigurationSqlite<,>));

        act.Should().Throw<ArgumentNullException>().WithParameterName("serviceProvider");
    }

    [Fact]
    public void ApplyAllConfigurations_NullModelBuilder_ThrowsArgumentNullException()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var assembly = typeof(ModelBuilderExtensionsTests).Assembly;

        var act = () => ModelBuilderExtensions.ApplyAllConfigurations(
            null!, sp, assembly, typeof(IEntityTypeConfigurationSqlite<,>));

        act.Should().Throw<ArgumentNullException>().WithParameterName("modelBuilder");
    }

    // ── Assembly with no matching types: no exception ──
    [Fact]
    public void ApplyAllConfigurations_AssemblyWithNoMatchingTypes_DoesNotThrow()
    {
        var modelBuilder = new ModelBuilder();
        var sp = new ServiceCollection().BuildServiceProvider();
        // Use an assembly that has no IEntityTypeConfigurationSqlite implementations
        var assembly = typeof(string).Assembly;

        var act = () => ModelBuilderExtensions.ApplyAllConfigurations(
            modelBuilder, sp, assembly, typeof(IEntityTypeConfigurationSqlite<,>));

        act.Should().NotThrow();
    }

    public sealed class TestMappedEntity : AuditableBaseEntity<int>
    {
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>
    /// A concrete configuration that implements the Sqlite interface and will be discovered
    /// by ApplyAllConfigurations when scanning this test assembly.
    /// </summary>
    public sealed class TestEntitySqliteConfiguration
        : IEntityTypeConfigurationSqlite<TestMappedEntity, int>
    {
        public void Configure(EntityTypeBuilder<TestMappedEntity> builder)
        {
            builder.HasKey(e => e.Id);
            builder.Property(e => e.Id).ValueGeneratedNever();
            builder.Property(e => e.Value).HasMaxLength(100);
        }
    }

    private sealed class TestDataSourceService : IDataSourceService
    {
        public DataSource GetDataSource(Type entityType, Type configurationType) => DataSource.Sqlite;

        public DataSource GetDataSource<TEntity, TIdentifierType, TEntityTypeConfiguration>()
            where TEntity : AuditableBaseEntity<TIdentifierType>
            where TEntityTypeConfiguration : class
            where TIdentifierType : notnull
            => DataSource.Sqlite;

        public DataSource GetDataSource(string entityFullName) => DataSource.Sqlite;

        public DataSource GetDataSource(Type entityType) => DataSource.Sqlite;

        public bool HaveIncludeSupport(DataSource first, DataSource second) => first == second;

        public bool HaveIncludeSupport(string firstEntityFullName, string secondEntityFullName) => true;
    }

    /// <summary>
    /// Minimal context that invokes ApplyAllConfigurations during OnModelCreating.
    /// </summary>
    public sealed class TestModelBuilderDbContext(DbContextOptions<TestModelBuilderDbContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var sp = new ServiceCollection()
                .AddSingleton<IDataSourceService>(new TestDataSourceService())
                .BuildServiceProvider();

            modelBuilder.ApplyAllConfigurations(
                sp,
                typeof(ModelBuilderExtensionsTests).Assembly,
                typeof(IEntityTypeConfigurationSqlite<,>));
        }
    }
}

using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

public sealed class EntityTypeConfigurationTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public EntityTypeConfigurationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void SqliteConfig_Configure_SetsTableNameAndKey()
    {
        var options = new DbContextOptionsBuilder<SqliteTestDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new SqliteTestDbContext(options);
        context.Database.EnsureCreated();

        var entityType = context.Model.FindEntityType(typeof(SqliteTestEntity));
        entityType.Should().NotBeNull();
        entityType!.GetTableName().Should().Be(nameof(SqliteTestEntity));
    }

    [Fact]
    public void SqliteConfig_Configure_SetsValueGeneratedNever_WhenNoAttribute()
    {
        var options = new DbContextOptionsBuilder<SqliteTestDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new SqliteTestDbContext(options);
        context.Database.EnsureCreated();

        var entityType = context.Model.FindEntityType(typeof(SqliteTestEntity));
        var idProperty = entityType!.FindProperty(nameof(SqliteTestEntity.Id));
        idProperty.Should().NotBeNull();
    }

    // -- Test entity (no IdValueGenerated attribute, so ValueGeneratedNever) --
    public sealed class SqliteTestEntity : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    // -- Concrete Sqlite configuration --
    public sealed class SqliteTestEntityConfig(IDataSourceService dataSourceService)
        : EntityTypeConfigurationSqlite<SqliteTestEntity, int>(dataSourceService);

    // -- Test DbContext using the Sqlite configuration --
    public sealed class SqliteTestDbContext(DbContextOptions options)
        : DbContext(options)
    {
        public DbSet<SqliteTestEntity> SqliteTestEntities => Set<SqliteTestEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var dataSourceService = new Mock<IDataSourceService>();
            dataSourceService
                .Setup(d => d.GetDataSource(It.IsAny<Type>(), It.IsAny<Type>()))
                .Returns(DataSource.Sqlite);

            var config = new SqliteTestEntityConfig(dataSourceService.Object);
            modelBuilder.Entity<SqliteTestEntity>(builder =>
            {
                config.Configure(builder);
                builder.Property(e => e.Name);
            });
        }
    }
}

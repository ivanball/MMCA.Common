using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Attributes;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;
using MMCA.Common.Infrastructure.Persistence.Conversions;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.DbContexts;

/// <summary>
/// The PostgreSQL engine's model, built offline (metadata only, nothing connects). Three things are
/// engine-specific and every one of them is a runtime failure rather than a compile error if it is
/// wrong: the partial-index predicates (PostgreSQL rejects the bracketed identifiers SQL Server and
/// SQLite both accept), the soft-delete predicate (its flag is a real boolean, so <c>= 0</c> is a
/// type error) and the timestamp mapping (Npgsql refuses a <see cref="DateTime"/> that is not UTC).
/// <para>
/// The SQL Server assertions at the end are the regression half: they prove the same code path still
/// produces exactly the literals it always did for the engines that already shipped.
/// </para>
/// </summary>
public sealed class PostgreSQLDbContextModelTests
{
    private const string PostgresConnectionString = "Host=localhost;Database=modeltests;Username=app";

    [Fact]
    public void PostgreSQLModel_MapsTheEntityLikeSqlServer_TableSchemaAndIdentityKey()
    {
        using var context = CreatePostgreSQLContext();

        var entityType = context.Model.FindEntityType(typeof(PostgresThing));

        entityType.Should().NotBeNull();
        entityType!.GetTableName().Should().Be(nameof(PostgresThing));
        entityType.GetSchema().Should().Be(
            "dbo",
            "PostgreSQL takes the SQL Server mapping unchanged so an entity moves between the two by "
            + "changing its configuration base class and nothing else");
        entityType.FindProperty(nameof(PostgresThing.Id))!.ValueGenerated
            .Should().Be(ValueGenerated.OnAdd);
    }

    [Fact]
    public void PostgreSQLModel_CreatesThePostgreSQLContextClass()
    {
        using var context = CreatePostgreSQLContext();

        context.Should().BeOfType<PostgreSQLDbContext>();
        context.DataSourceKey.Engine.Should().Be(DataSource.PostgreSQL);
    }

    // Npgsql maps DateTime to timestamptz and throws on a value whose Kind is not Utc. Both the
    // non-nullable and the nullable form must carry the normalizing converter, because the framework's
    // own tables use both (OutboxMessage.OccurredOn and .ProcessedOn).
    [Theory]
    [InlineData(nameof(OutboxMessage.OccurredOn))]
    [InlineData(nameof(OutboxMessage.ProcessedOn))]
    [InlineData(nameof(OutboxMessage.LockedUntil))]
    public void PostgreSQLModel_MapsEveryTimestampAsUtcTimestampWithTimeZone(string propertyName)
    {
        using var context = CreatePostgreSQLContext();

        var property = context.Model.FindEntityType(typeof(OutboxMessage))!.FindProperty(propertyName);

        property.Should().NotBeNull();
        property!.GetColumnType().Should().Be(PostgreSQLDbContext.TimestampWithTimeZone);
        property.GetValueConverter().Should().BeOfType<UtcDateTimeConverter>();
    }

    [Fact]
    public void PostgreSQLModel_MapsAConsumerEntitysTimestampsToo()
    {
        using var context = CreatePostgreSQLContext();

        var createdOn = context.Model.FindEntityType(typeof(PostgresThing))!
            .FindProperty(nameof(PostgresThing.CreatedOn));

        createdOn!.GetValueConverter().Should().BeOfType<UtcDateTimeConverter>(
            "the audit stamps are written by the framework but live on the consumer's entity");
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Utc)]
    public void UtcDateTimeConverter_WritesUtc(DateTimeKind kind)
    {
        var value = new DateTime(2026, 9, 9, 12, 0, 0, kind);

        var written = (DateTime)new UtcDateTimeConverter().ConvertToProvider(value)!;

        written.Kind.Should().Be(DateTimeKind.Utc);
        written.Should().Be(DateTime.SpecifyKind(value, DateTimeKind.Utc), "an unzoned value is already UTC by convention");
    }

    [Fact]
    public void UtcDateTimeConverter_ConvertsALocalValueRatherThanRelabellingIt()
    {
        var local = new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Local);

        var written = (DateTime)new UtcDateTimeConverter().ConvertToProvider(local)!;

        written.Kind.Should().Be(DateTimeKind.Utc);
        written.Should().Be(local.ToUniversalTime());
    }

    [Fact]
    public void UtcDateTimeConverter_ReadsBackAsUtc()
    {
        var stored = new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Unspecified);

        var read = (DateTime)new UtcDateTimeConverter().ConvertFromProvider(stored)!;

        read.Kind.Should().Be(DateTimeKind.Utc);
    }

    // The three outbox partial indexes. PostgreSQL rejects [Bracketed] identifiers outright, so a
    // filter that keeps the SQL Server spelling fails at CREATE INDEX, not at model build.
    [Fact]
    public void PostgreSQLModel_QuotesTheOutboxIndexPredicatesForPostgreSQL()
    {
        using var context = CreatePostgreSQLContext();

        var filters = OutboxFilters(context);

        filters.Should().BeEquivalentTo(
            "\"ProcessedOn\" IS NULL",
            "\"ProcessedOn\" IS NOT NULL",
            "\"OrderingKey\" IS NOT NULL AND \"ProcessedOn\" IS NULL");
    }

    // PostgreSQL stores the soft-delete flag as a real boolean, and "IsDeleted = 0" is a type error
    // there rather than a false predicate.
    [Fact]
    public void PostgreSQLModel_FiltersAUniqueIndexOnTheBooleanSoftDeleteFlag()
    {
        using var context = CreatePostgreSQLContext();

        var index = context.Model.FindEntityType(typeof(PostgresThing))!
            .GetIndexes()
            .Single(i => i.IsUnique);

        index.GetFilter().Should().Be("\"IsDeleted\" = false");
    }

    // The regression half: the engines that already shipped must produce byte-identical predicates.
    [Fact]
    public void SqlServerModel_KeepsTheBracketedPredicatesItAlwaysProduced()
    {
        using var context = CreateSqlServerContext();

        OutboxFilters(context).Should().BeEquivalentTo(
            "[ProcessedOn] IS NULL",
            "[ProcessedOn] IS NOT NULL",
            "[OrderingKey] IS NOT NULL AND [ProcessedOn] IS NULL");

        context.Model.FindEntityType(typeof(SqlServerThing))!
            .GetIndexes()
            .Single(i => i.IsUnique)
            .GetFilter()
            .Should().Be("[IsDeleted] = 0");
    }

    private static List<string?> OutboxFilters(ApplicationDbContext context) =>
        [.. context.Model.FindEntityType(typeof(OutboxMessage))!
            .GetIndexes()
            .Select(i => i.GetFilter())
            .Where(f => f is not null)];

    private static ApplicationDbContext CreatePostgreSQLContext() =>
        CreateContext(DataSource.PostgreSQL, "PostgresModel");

    // A named source rather than Default on purpose: EF caches one model per (context type, physical
    // source name) for the life of the process, so a test that built the SQL Server Default model
    // first would decide this one's contents.
    private static ApplicationDbContext CreateSqlServerContext() =>
        CreateContext(DataSource.SQLServer, "SqlServerModel");

    /// <summary>
    /// Builds one engine's context through the real resolver, registry and physical factory, so the
    /// model under assertion is the one a host gets rather than a hand-assembled stand-in.
    /// </summary>
    private static ApplicationDbContext CreateContext(DataSource engine, string logicalName)
    {
        var connectionStrings = new ConnectionStringSettings { SQLServerConnectionString = "Server=unused;Database=unused;" };
        var dataSources = new DataSourcesSettings(new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
        {
            ["PostgresModel"] = new() { PostgreSQLConnectionString = PostgresConnectionString },
            ["SqlServerModel"] = new() { SQLServerConnectionString = "Server=unused;Database=modeltests;" },
        });

        var resolver = new DataSourceResolver(Options.Create(connectionStrings), dataSources, NullLogger<DataSourceResolver>.Instance);
        var assemblyProvider = new FixedAssemblyProvider();
        var registry = new EntityDataSourceRegistry(assemblyProvider, resolver);

        var serviceProvider = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton(Mock.Of<IDomainEventDispatcher>())
            .AddSingleton<IOutboxSignal, OutboxSignal>()
            .AddSingleton<AuditSaveChangesInterceptor>()
            .AddSingleton<DomainEventSaveChangesInterceptor>()
            .AddSingleton<IEntityDataSourceRegistry>(registry)
            .AddSingleton<IDataSourceResolver>(resolver)
            .BuildServiceProvider();

        var physicalFactory = new PhysicalDbContextFactory(serviceProvider, resolver, assemblyProvider);

        return physicalFactory.Create(resolver.ResolveLogical(engine, logicalName));
    }

    private sealed class FixedAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<System.Reflection.Assembly> GetConfigurationAssemblies() =>
            [typeof(PostgreSQLDbContextModelTests).Assembly];
    }

    [IdValueGenerated]
    public sealed class PostgresThing : AuditableAggregateRootEntity<int>
    {
        public string Code { get; set; } = string.Empty;
    }

    [IdValueGenerated]
    public sealed class SqlServerThing : AuditableAggregateRootEntity<int>
    {
        public string Code { get; set; } = string.Empty;
    }

    [UseDatabase("PostgresModel")]
    private sealed class PostgresThingConfiguration : EntityTypeConfigurationPostgreSQL<PostgresThing, int>
    {
        public override void Configure(EntityTypeBuilder<PostgresThing> builder)
        {
            base.Configure(builder);

            // Unique, so SoftDeleteUniqueIndexConvention narrows it to the live rows.
            builder.HasIndex(p => p.Code).IsUnique();
        }
    }

    [UseDatabase("SqlServerModel")]
    private sealed class SqlServerThingConfiguration : EntityTypeConfigurationSQLServer<SqlServerThing, int>
    {
        public override void Configure(EntityTypeBuilder<SqlServerThing> builder)
        {
            base.Configure(builder);

            builder.HasIndex(p => p.Code).IsUnique();
        }
    }
}

using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Settings;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.DataSources;

/// <summary>
/// Proves that an entity configuration body is <b>portable across engines</b>: a Cosmos-targeted
/// configuration can keep relational-only constructs (a filtered index, <c>HasMaxLength</c>) and a
/// cross-source relationship, and the framework strips/degrades them automatically while building the
/// Cosmos model. This is what lets a consumer move an entity between engines by changing only the
/// engine declaration (<see cref="UseDataSourceAttribute"/>) with no configuration-body edits. The
/// model is built offline (no live Cosmos connection is needed for metadata).
/// </summary>
public sealed class CosmosConfigurationPortabilityTests
{
    // Well-known Azure Cosmos DB emulator connection string. Only its format is used here — the model
    // is built offline, nothing connects.
    private const string EmulatorConnectionString =
        "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;";

    [Fact]
    public void CosmosModel_StripsRelationalIndexes_AndDegradesCrossSourceRelationship()
    {
        // Arrange: PortableThing -> Cosmos (its config keeps a relational filtered index + a
        // cross-source HasOne to a SQL Server principal). PortablePrincipal -> SQL Server (Default).
        var connectionStrings = new ConnectionStringSettings { SQLServerConnectionString = "Server=unused;" };
        var dataSources = new DataSourcesSettings(new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
        {
            ["Portable"] = new() { CosmosConnectionString = EmulatorConnectionString, CosmosDatabaseName = "TestDb" },
        });

        var resolver = new DataSourceResolver(connectionStrings, dataSources, NullLogger<DataSourceResolver>.Instance);
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

        // Act: build the Cosmos model (metadata only — offline).
        using var context = physicalFactory.Create(resolver.ResolveLogical(DataSource.CosmosDB, "Portable"));
        var model = context.Model;
        var thing = model.FindEntityType(typeof(PortableThing));

        // Assert
        thing.Should().NotBeNull("the Cosmos entity is mapped");
        thing!.GetIndexes().Should().BeEmpty("the Cosmos provider strips relational index definitions");
        thing.FindProperty(nameof(PortableThing.PrincipalId))
            .Should().NotBeNull("the scalar FK column survives the cross-source degrade");
        thing.GetForeignKeys().Should().BeEmpty("the cross-source FK relationship is removed");
        model.FindEntityType(typeof(PortablePrincipal))
            .Should().BeNull("the SQL Server principal is foreign to the Cosmos model and is dropped");
    }

    private sealed class FixedAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<System.Reflection.Assembly> GetConfigurationAssemblies() =>
            [typeof(CosmosConfigurationPortabilityTests).Assembly];
    }

    public sealed class PortableThing : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;

        public int PrincipalId { get; set; }

        public PortablePrincipal? Principal { get; set; }
    }

    public sealed class PortablePrincipal : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    // Cosmos config that deliberately keeps relational-only constructs + a cross-source relationship,
    // to prove the framework makes them portable rather than requiring the author to remove them.
    [UseDataSource(DataSource.CosmosDB)]
    [UseDatabase("Portable")]
    private sealed class PortableThingConfiguration : EntityTypeConfiguration<PortableThing, int>
    {
        public override void Configure(EntityTypeBuilder<PortableThing> builder)
        {
            base.Configure(builder);

            builder.Property(p => p.Name).HasMaxLength(100);
            builder.HasIndex(p => p.PrincipalId).HasFilter("[IsDeleted] = 0");
            builder.HasOne(p => p.Principal).WithMany().HasForeignKey(p => p.PrincipalId);
        }
    }

    [UseDatabase("Default")]
    private sealed class PortablePrincipalConfiguration : EntityTypeConfigurationSQLServer<PortablePrincipal, int>;
}

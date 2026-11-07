using System.Reflection;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.ViewAs;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Auth.ViewAs;

/// <summary>
/// The support access log's display snapshot columns (ADR-137): <c>StaffDisplay</c> and
/// <c>TargetDisplay</c> on <c>ViewAsGrants</c>, both nullable (a row written before the snapshot, or
/// one whose account was erased, has none) and as wide as <see cref="ViewAsGrant.DisplayMaxLength"/>.
/// Asserted on the real model each relational engine builds through <see cref="ApplicationDbContext"/>,
/// since every relational engine maps the table (Cosmos never reaches the gate).
/// </summary>
public sealed class ViewAsGrantSnapshotModelTests
{
    public static TheoryData<DataSource> RelationalEngines => [DataSource.SQLServer, DataSource.PostgreSQL, DataSource.Sqlite];

    // Spec A assertion 3 (model half): the two snapshot columns exist on every engine that maps the table.
    [Theory]
    [MemberData(nameof(RelationalEngines))]
    public void TheGrantTable_CarriesBothNullableDisplaySnapshotColumns_OnEveryRelationalEngine(DataSource engine)
    {
        using var context = EngineContext.Create(engine);

        var entityType = context.Model.FindEntityType(typeof(ViewAsGrant));
        entityType.Should().NotBeNull($"ViewAs:Enabled maps the grant table on {engine}");

        foreach (var name in new[] { nameof(ViewAsGrant.StaffDisplay), nameof(ViewAsGrant.TargetDisplay) })
        {
            var property = entityType!.FindProperty(name);
            property.Should().NotBeNull($"{name} is a column on {engine}");
            property!.IsNullable.Should().BeTrue($"{name} is blanked by erasure and absent on older rows");
            property.GetMaxLength().Should().Be(ViewAsGrant.DisplayMaxLength, $"{name} is bounded on {engine}");
        }
    }

    /// <summary>
    /// A context that runs the real <see cref="ApplicationDbContext.OnModelCreating"/> (so the real
    /// view-as gate) against the given engine's provider, with view-as on for the default source.
    /// </summary>
    private sealed class EngineContext : ApplicationDbContext
    {
        private EngineContext(
            DbContextOptions<EngineContext> options,
            IServiceProvider serviceProvider,
            PhysicalDataSource physicalDataSource)
            : base(options, serviceProvider, new NullAssemblyProvider(), physicalDataSource)
        {
        }

        public static EngineContext Create(DataSource engine)
        {
            var services = new ServiceCollection();
            services.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
            services.AddSingleton(_ => new DomainEventSaveChangesInterceptor(
                Mock.Of<IDomainEventDispatcher>(),
                Mock.Of<ILogger<DomainEventSaveChangesInterceptor>>(),
                Mock.Of<IOutboxSignal>(),
                timeProvider: TimeProvider.System));
            services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
            services.AddSingleton<IOptions<ViewAsSettings>>(Options.Create(new ViewAsSettings
            {
                Enabled = true,
                DataSourceName = DataSourceKey.DefaultName,
            }));

            // One context type serves three providers, so EF's per-options service-provider cache (and
            // the model it holds) must be off, or every engine after the first would reuse the first
            // engine's model (the SchedulerModelGateTests reason).
            var builder = new DbContextOptionsBuilder<EngineContext>().EnableServiceProviderCaching(false);
            PhysicalDataSource physical;
            if (engine == DataSource.SQLServer)
            {
                builder.UseSqlServer("Server=test;Database=test");
                physical = TestPhysicalDataSources.SqlServer();
            }
            else if (engine == DataSource.PostgreSQL)
            {
                builder.UseNpgsql("Host=localhost;Database=test;Username=app");
                physical = TestPhysicalDataSources.Postgres();
            }
            else
            {
                builder.UseSqlite("DataSource=:memory:");
                physical = TestPhysicalDataSources.Sqlite();
            }

            physical.Key.Name.Should().Be(DataSourceKey.DefaultName, "the test doubles target the default source");
            return new EngineContext(builder.Options, services.BuildServiceProvider(), physical);
        }
    }

    private sealed class NullAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<Assembly> GetConfigurationAssemblies() => [];
    }
}

using System.Reflection;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.AuditTrail;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Infrastructure.Tests.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.AuditTrail;

/// <summary>
/// The <c>AuditTrailEntries</c> table is settings-gated: a host that never opted in must get exactly
/// the model it had before the trail shipped, so its migrations never see the table. Unlike the
/// host-scoped job table it belongs in EVERY relational source, because a trail row commits in the
/// same transaction as the change it describes. These tests build the real
/// <see cref="ApplicationDbContext"/> model under each combination.
/// </summary>
public sealed class AuditTrailModelGateTests : ModelGateTestsBase
{
    protected override Type GatedEntityType => typeof(AuditTrailEntry);

    [Fact]
    public void Model_AuditTrailEnabledOnANonDefaultSource_StillContainsTheTrailTable()
    {
        using var context = GateTestContext.Create(auditTrailEnabled: true, sourceName: "Conference");

        context.Model.FindEntityType(typeof(AuditTrailEntry)).Should().NotBeNull(
            "a trail row must land in the same database as the change it describes, so every "
            + "relational source needs the table");
    }

    // Service-provider caching is off in GateTestContext, so the per-case marker is not needed here.
    protected override DbContext CreateGateContext<TCase>(GateState state) => state switch
    {
        GateState.NeverRegistered => GateTestContext.Create(
            auditTrailEnabled: false, sourceName: DataSourceKey.DefaultName, registerSettings: false),
        GateState.Disabled => GateTestContext.Create(auditTrailEnabled: false, sourceName: DataSourceKey.DefaultName),
        GateState.Enabled => GateTestContext.Create(auditTrailEnabled: true, sourceName: DataSourceKey.DefaultName),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    protected override void AssertEnabledMapping(IEntityType entity)
    {
        entity.GetTableName().Should().Be("AuditTrailEntries");
        entity.FindPrimaryKey()!.Properties.Select(p => p.Name).Should().Equal(nameof(AuditTrailEntry.Id));
        entity.GetIndexes()
            .Select(index => string.Join(",", index.Properties.Select(p => p.Name)))
            .Should().Contain("EntityType,EntityKey,ChangedOn");
    }

    /// <summary>
    /// A context that runs the real <see cref="ApplicationDbContext.OnModelCreating"/> (and therefore
    /// the real gate) with a configurable data source and audit-trail setting.
    /// </summary>
    private sealed class GateTestContext : ApplicationDbContext
    {
        private GateTestContext(
            DbContextOptions<GateTestContext> options,
            IServiceProvider serviceProvider,
            PhysicalDataSource physicalDataSource)
            : base(options, serviceProvider, new NullAssemblyProvider(), physicalDataSource)
        {
        }

        public static GateTestContext Create(bool auditTrailEnabled, string sourceName, bool registerSettings = true)
        {
            var services = new ServiceCollection();
            services.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
            services.AddSingleton(_ =>
            {
                var dispatcher = new Mock<IDomainEventDispatcher>();
                var logger = new Mock<ILogger<DomainEventSaveChangesInterceptor>>();
                var outboxSignal = new Mock<IOutboxSignal>();
                return new DomainEventSaveChangesInterceptor(dispatcher.Object, logger.Object, outboxSignal.Object, timeProvider: TimeProvider.System);
            });
            services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());

            if (registerSettings)
            {
                services.AddSingleton<IOptions<AuditTrailSettings>>(
                    Options.Create(new AuditTrailSettings { Enabled = auditTrailEnabled }));
            }

            // EF caches its internal service provider (and with it the built model) per distinct set
            // of options, and DataSourceModelCacheKeyFactory keys that model by (context type,
            // source name) only. These tests build the SAME context type against the SAME source
            // name under different settings, which in production never happens (the flag is fixed
            // for the life of a host), so caching must be off here.
            var options = new DbContextOptionsBuilder<GateTestContext>()
                .UseSqlite("DataSource=:memory:")
                .EnableServiceProviderCaching(false)
                .Options;

            var physical = new PhysicalDataSource(
                new DataSourceKey(DataSource.Sqlite, sourceName), "DataSource=:memory:", null, "Test");

            return new GateTestContext(options, services.BuildServiceProvider(), physical);
        }
    }

    private sealed class NullAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<Assembly> GetConfigurationAssemblies() => [];
    }
}

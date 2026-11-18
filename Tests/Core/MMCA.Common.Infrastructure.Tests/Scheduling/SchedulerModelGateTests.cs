using System.Reflection;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Infrastructure.Scheduling;
using MMCA.Common.Infrastructure.Tests.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Scheduling;

/// <summary>
/// The <c>ScheduledJobs</c> table is settings-gated: a host that never opted in must get exactly the
/// model it had before the scheduler shipped, so its migrations never see the table. These tests
/// build the real <see cref="ApplicationDbContext"/> model under each combination of
/// <c>Scheduler:Enabled</c> and data source.
/// </summary>
public sealed class SchedulerModelGateTests : ModelGateTestsBase
{
    protected override Type GatedEntityType => typeof(ScheduledJobEntry);

    [Fact]
    public void Model_SchedulerEnabledOnANonDefaultSource_DoesNotContainTheJobTable()
    {
        using var context = GateTestContext.Create(schedulerEnabled: true, sourceName: "Conference");

        context.Model.FindEntityType(typeof(ScheduledJobEntry)).Should().BeNull(
            "jobs are host-scoped: the table lives in exactly one database, unlike the per-source outbox");
    }

    // Service-provider caching is off in GateTestContext, so the per-case marker is not needed here.
    protected override DbContext CreateGateContext<TCase>(GateState state) => state switch
    {
        GateState.NeverRegistered => GateTestContext.Create(
            schedulerEnabled: false, sourceName: DataSourceKey.DefaultName, registerSettings: false),
        GateState.Disabled => GateTestContext.Create(schedulerEnabled: false, sourceName: DataSourceKey.DefaultName),
        GateState.Enabled => GateTestContext.Create(schedulerEnabled: true, sourceName: DataSourceKey.DefaultName),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    protected override void AssertEnabledMapping(IEntityType entity)
    {
        entity.GetTableName().Should().Be("ScheduledJobs");
        entity.FindPrimaryKey()!.Properties.Select(p => p.Name).Should().Equal(nameof(ScheduledJobEntry.JobName));
    }

    /// <summary>
    /// A context that runs the real <see cref="ApplicationDbContext.OnModelCreating"/> (and therefore
    /// the real gate) with a configurable data source and scheduler setting.
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

        public static GateTestContext Create(bool schedulerEnabled, string sourceName, bool registerSettings = true)
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
                services.AddSingleton<IOptions<SchedulerSettings>>(
                    Options.Create(new SchedulerSettings { Enabled = schedulerEnabled }));
            }

            // EF caches its internal service provider (and with it the built model) per distinct set
            // of options, and DataSourceModelCacheKeyFactory keys that model by (context type,
            // source name) only. These tests build the SAME context type against the SAME source
            // name under different scheduler settings, which in production never happens (the flag is
            // fixed for the life of a host), so caching must be off here or every case after the
            // first would silently assert against the first case's model.
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

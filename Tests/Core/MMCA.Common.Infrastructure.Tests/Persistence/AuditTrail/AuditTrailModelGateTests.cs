using System.Reflection;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.AuditTrail;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Settings;
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
public sealed class AuditTrailModelGateTests
{
    [Fact]
    public void Model_AuditTrailDisabled_DoesNotContainTheTrailTable()
    {
        using var context = GateTestContext.Create(auditTrailEnabled: false, sourceName: DataSourceKey.DefaultName);

        context.Model.FindEntityType(typeof(AuditTrailEntry)).Should().BeNull(
            "a host that never called AddAuditTrail must not gain a table in its next migration");
    }

    [Fact]
    public void Model_NoAuditTrailSettingsRegisteredAtAll_DoesNotContainTheTrailTable()
    {
        using var context = GateTestContext.Create(
            auditTrailEnabled: false, sourceName: DataSourceKey.DefaultName, registerSettings: false);

        context.Model.FindEntityType(typeof(AuditTrailEntry)).Should().BeNull(
            "the absence of the settings must read as disabled, not fail context construction");
    }

    [Fact]
    public void Model_AuditTrailEnabled_ContainsTheTrailTableWithItsReadIndex()
    {
        using var context = GateTestContext.Create(auditTrailEnabled: true, sourceName: DataSourceKey.DefaultName);

        var entityType = context.Model.FindEntityType(typeof(AuditTrailEntry));
        entityType.Should().NotBeNull();
        entityType!.GetTableName().Should().Be("AuditTrailEntries");
        entityType.FindPrimaryKey()!.Properties.Select(p => p.Name).Should().Equal(nameof(AuditTrailEntry.Id));
        entityType.GetIndexes()
            .Select(index => string.Join(",", index.Properties.Select(p => p.Name)))
            .Should().Contain("EntityType,EntityKey,ChangedOn");
    }

    [Fact]
    public void Model_AuditTrailEnabledOnANonDefaultSource_StillContainsTheTrailTable()
    {
        using var context = GateTestContext.Create(auditTrailEnabled: true, sourceName: "Conference");

        context.Model.FindEntityType(typeof(AuditTrailEntry)).Should().NotBeNull(
            "a trail row must land in the same database as the change it describes, so every "
            + "relational source needs the table");
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
                return new DomainEventSaveChangesInterceptor(dispatcher.Object, logger.Object, outboxSignal.Object);
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

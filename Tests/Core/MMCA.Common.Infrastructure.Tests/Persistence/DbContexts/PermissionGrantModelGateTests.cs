using System.Diagnostics.CodeAnalysis;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.Permissions;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Infrastructure.Persistence.Auth;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.DbContexts;

/// <summary>
/// The <c>PermissionGrants</c> table is mapped by <see cref="ApplicationDbContext"/> itself, for the
/// reason the refresh sessions are: a consumer running on the sealed engine contexts (ADR-006) has
/// no context class to override, and the grant entity is not an <c>AuditableBaseEntity</c>, so the
/// module entity-configuration mechanism never sees it. That makes the gate the whole contract, and
/// it is asserted against a real built model. Unlike the sessions, the opt-in is the
/// <c>AddStoredPermissionGrants</c> registration rather than a configuration flag, so the gate reads
/// a marker: <see cref="PermissionGrantModelGate"/>.
/// </summary>
/// <remarks>
/// Every case gets its OWN context type (the <c>Case*</c> markers). EF caches a built model per
/// <see cref="DataSourceModelCacheKeyFactory"/> key, which is (context type, physical source name),
/// and that cache is process-wide: two cases sharing a type and a source name would share one model,
/// so whichever ran first would decide the answer for both and the suite would pass or fail by test
/// order. A marker per case makes each key unique and the class order-independent.
/// </remarks>
public sealed class PermissionGrantModelGateTests : ModelGateTestsBase
{
    private const string IdentitySource = "Identity";

    protected override Type GatedEntityType => typeof(PermissionGrant);

    [Fact]
    public void WhenOptedInForANamedSource_OnlyThatSourcesContextGetsTheTable()
    {
        var settings = new PermissionGrantSettings { DataSourceName = IdentitySource };

        using var identity = CreateContext<CaseNamedSource>(optedIn: true, settings, IdentitySource);
        using var conference = CreateContext<CaseNamedSource>(optedIn: true, settings, "Conference");
        using var @default = CreateContext<CaseNamedSource>(optedIn: true, settings, DataSourceKey.DefaultName);

        identity.Model.FindEntityType(typeof(PermissionGrant)).Should().NotBeNull();
        conference.Model.FindEntityType(typeof(PermissionGrant)).Should().BeNull(
            "grants are Identity-module data, not per-source infrastructure like the outbox");
        @default.Model.FindEntityType(typeof(PermissionGrant)).Should().BeNull(
            "naming a source moves the table there rather than adding a second copy");
    }

    [Fact]
    public void WhenOptedInOnTheMatchingSource_TheMappingIsTheOneApplyPermissionGrantConfigurationProduces()
    {
        using var context = CreateContext<CaseMappingShape>(
            optedIn: true,
            settings: new PermissionGrantSettings { DataSourceName = IdentitySource },
            dataSourceName: IdentitySource);

        var entity = context.Model.FindEntityType(typeof(PermissionGrant))!;

        entity.FindProperty(nameof(PermissionGrant.Role))!.GetMaxLength().Should().Be(PermissionGrant.RoleMaxLength);
        entity.GetIndexes().Should().ContainSingle(index =>
            index.IsUnique
            && index.GetDatabaseName() == PermissionGrantModelBuilderExtensions.RolePermissionIndexName);
    }

    // The opt-in here is the AddStoredPermissionGrants registration, not a settings flag: the settings
    // class binds to defaults for any host, so the Disabled state registers the settings WITHOUT the
    // gate marker, proving the settings alone can never be the gate.
    protected override DbContext CreateGateContext<TCase>(GateState state) => state switch
    {
        GateState.NeverRegistered => CreateContext<TCase>(optedIn: false, settings: null, DataSourceKey.DefaultName),
        GateState.Disabled => CreateContext<TCase>(optedIn: false, new PermissionGrantSettings(), DataSourceKey.DefaultName),
        GateState.Enabled => CreateContext<TCase>(optedIn: true, new PermissionGrantSettings(), DataSourceKey.DefaultName),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    protected override void AssertEnabledMapping(IEntityType entity) =>
        entity.GetTableName().Should().Be(PermissionGrantModelBuilderExtensions.TableName);

    /// <summary>
    /// Builds a real context over <see cref="ApplicationDbContext"/> for one physical source and one
    /// opt-in state. Nothing connects: the assertions read <c>context.Model</c>, which EF builds
    /// without touching a server.
    /// </summary>
    private static GateContext<TCase> CreateContext<TCase>(
        bool optedIn,
        PermissionGrantSettings? settings,
        string dataSourceName)
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AuditSaveChangesInterceptor>();
        services.AddSingleton(Mock.Of<IDomainEventDispatcher>());
        services.AddSingleton(Mock.Of<IOutboxSignal>());
        services.AddSingleton<ILogger<DomainEventSaveChangesInterceptor>>(
            NullLogger<DomainEventSaveChangesInterceptor>.Instance);
        services.AddSingleton<DomainEventSaveChangesInterceptor>();
        services.AddSingleton(Mock.Of<IEntityDataSourceRegistry>());

        if (optedIn)
        {
            services.AddSingleton<PermissionGrantModelGate>();
        }

        if (settings is not null)
        {
            services.AddSingleton<IOptions<PermissionGrantSettings>>(Options.Create(settings));
        }

        var assemblyProvider = new Mock<IEntityConfigurationAssemblyProvider>();
        assemblyProvider.Setup(x => x.GetConfigurationAssemblies()).Returns([]);

        return new GateContext<TCase>(
            new DbContextOptionsBuilder<GateContext<TCase>>().Options,
            services.BuildServiceProvider(),
            assemblyProvider.Object,
            new PhysicalDataSource(
                new DataSourceKey(DataSource.SQLServer, dataSourceName),
                GateContext<TCase>.ConnectionString,
                MigrationsAssembly: null,
                CosmosDatabaseName: string.Empty));
    }

    // One closed type per case; see the class remarks for why the model cache demands it (the
    // shared cases bring their own markers from ModelGateTestsBase).
    private sealed class CaseNamedSource;

    private sealed class CaseMappingShape;

    /// <summary>
    /// A minimal relational context over the shared base, standing in for the sealed
    /// <c>SQLServerDbContext</c>: the gate under test lives in <see cref="ApplicationDbContext"/>,
    /// and skipping the module configuration scan keeps the built model down to what the base maps.
    /// </summary>
    /// <typeparam name="TCase">Marker that gives each test case its own EF model cache entry.</typeparam>
    [SuppressMessage(
        "Major Code Smell",
        "S2326:Unused type parameters should be removed",
        Justification = "The parameter is deliberately phantom: its only job is to make each closed type distinct, which is what gives each test case its own EF model cache entry.")]
    private sealed class GateContext<TCase>(
        DbContextOptions options,
        IServiceProvider serviceProvider,
        IEntityConfigurationAssemblyProvider assemblyProvider,
        PhysicalDataSource physicalDataSource)
        : ApplicationDbContext(options, serviceProvider, assemblyProvider, physicalDataSource)
    {
        /// <summary>Never dialled: a provider is registered only because model building needs one.</summary>
        internal const string ConnectionString = "Server=(local);Database=model-only;Trusted_Connection=True;";

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlServer(ConnectionString);
            base.OnConfiguring(optionsBuilder);
        }
    }
}

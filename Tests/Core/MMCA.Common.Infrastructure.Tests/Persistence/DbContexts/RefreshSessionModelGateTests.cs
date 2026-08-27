using System.Diagnostics.CodeAnalysis;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Infrastructure.Persistence.Auth;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.DbContexts;

/// <summary>
/// The <c>RefreshSessions</c> table is mapped by <see cref="ApplicationDbContext"/> itself, because a
/// consumer running on the sealed engine contexts (ADR-006) has no context class to override and the
/// session entity is not an <c>AuditableBaseEntity</c>, so the module entity-configuration mechanism
/// never sees it. That makes the gate the whole contract, and it is asserted against a real built
/// model: a host that has not opted in must get exactly the model it had before sessions shipped
/// (otherwise every existing consumer's next migration grows a table it never asked for), and an
/// opted-in multi-source host must get the table in exactly one database.
/// </summary>
/// <remarks>
/// Every case gets its OWN context type (the <c>Case*</c> markers). EF caches a built model per
/// <see cref="DataSourceModelCacheKeyFactory"/> key, which is (context type, physical source name),
/// and that cache is process-wide: two cases sharing a type and a source name would share one model,
/// so whichever ran first would decide the answer for both and the suite would pass or fail by test
/// order. A marker per case makes each key unique and the class order-independent.
/// </remarks>
public sealed class RefreshSessionModelGateTests
{
    private const string IdentitySource = "Identity";

    [Fact]
    public void WithNoRefreshSessionSettingsRegistered_TheTableIsNotInTheModel()
    {
        using var context = CreateContext<CaseNoSettings>(settings: null, dataSourceName: DataSourceKey.DefaultName);

        context.Model.FindEntityType(typeof(RefreshSession)).Should().BeNull(
            "a host that never registered the options must build the model it had before sessions shipped");
    }

    [Fact]
    public void WithTheDefaultSettings_TheTableIsNotInTheModel()
    {
        using var context = CreateContext<CaseDefaultSettings>(new RefreshSessionSettings(), DataSourceKey.DefaultName);

        context.Model.FindEntityType(typeof(RefreshSession)).Should().BeNull(
            "Enabled defaults to false, so opting in is explicit and no existing consumer sees a migration");
    }

    [Fact]
    public void WhenEnabledOnTheDefaultSource_TheSingleDatabaseHostGetsTheTable()
    {
        using var context = CreateContext<CaseEnabledDefaultSource>(
            new RefreshSessionSettings { Enabled = true },
            DataSourceKey.DefaultName);

        var entity = context.Model.FindEntityType(typeof(RefreshSession));

        entity.Should().NotBeNull();
        entity!.GetTableName().Should().Be(RefreshSessionModelBuilderExtensions.TableName);
    }

    [Fact]
    public void WhenEnabledForANamedSource_OnlyThatSourcesContextGetsTheTable()
    {
        var settings = new RefreshSessionSettings { Enabled = true, DataSourceName = IdentitySource };

        using var identity = CreateContext<CaseNamedSource>(settings, IdentitySource);
        using var conference = CreateContext<CaseNamedSource>(settings, "Conference");
        using var @default = CreateContext<CaseNamedSource>(settings, DataSourceKey.DefaultName);

        identity.Model.FindEntityType(typeof(RefreshSession)).Should().NotBeNull();
        conference.Model.FindEntityType(typeof(RefreshSession)).Should().BeNull(
            "sessions are Identity-module data, not per-source infrastructure like the outbox");
        @default.Model.FindEntityType(typeof(RefreshSession)).Should().BeNull(
            "naming a source moves the table there rather than adding a second copy");
    }

    [Fact]
    public void WhenEnabledOnTheMatchingSource_TheMappingIsTheOneApplyRefreshSessionConfigurationProduces()
    {
        using var context = CreateContext<CaseMappingShape>(
            new RefreshSessionSettings { Enabled = true, DataSourceName = IdentitySource },
            IdentitySource);

        var entity = context.Model.FindEntityType(typeof(RefreshSession))!;

        entity.FindProperty(nameof(RefreshSession.TokenHash))!.GetMaxLength().Should().Be(RefreshSession.TokenHashLength);
        entity.GetIndexes().Should().ContainSingle(i =>
            i.IsUnique && i.Properties.Count == 1 && i.Properties[0].Name == nameof(RefreshSession.TokenHash));
    }

    /// <summary>
    /// Builds a real context over <see cref="ApplicationDbContext"/> for one physical source and one
    /// settings state. Nothing connects: the assertions read <c>context.Model</c>, which EF builds
    /// without touching a server.
    /// </summary>
    private static GateContext<TCase> CreateContext<TCase>(RefreshSessionSettings? settings, string dataSourceName)
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

        if (settings is not null)
        {
            services.AddSingleton<IOptions<RefreshSessionSettings>>(Options.Create(settings));
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
                SqlServerMigrationsAssembly: null,
                CosmosDatabaseName: string.Empty));
    }

    // One closed type per case; see the class remarks for why the model cache demands it.
    private sealed class CaseNoSettings;

    private sealed class CaseDefaultSettings;

    private sealed class CaseEnabledDefaultSource;

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

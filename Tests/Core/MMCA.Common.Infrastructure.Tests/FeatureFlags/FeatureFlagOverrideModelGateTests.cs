using System.Diagnostics.CodeAnalysis;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.FeatureFlags;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.FeatureFlags;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.FeatureFlags;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.FeatureFlags;

/// <summary>
/// The <c>FeatureFlagOverrides</c> table is mapped only when the host opted in, and only in the owning
/// service's own database (the <c>PermissionGrantModelGate</c> precedent): a host that never calls
/// <c>AddReloadableFeatureFlags</c> builds the model it had before, and a multi-database host gets the
/// table in exactly the source <c>FeatureFlags:DataSourceName</c> names.
/// </summary>
/// <remarks>
/// One context type per case, because EF caches a built model per (context type, physical source
/// name) for the whole process: see <c>PermissionGrantModelGateTests</c>.
/// </remarks>
public sealed class FeatureFlagOverrideModelGateTests
{
    private const string ConferenceSource = "Conference";

    [Fact]
    public void WithoutTheOptIn_TheTableIsNotInTheModel()
    {
        using var context = CreateContext<CaseNoOptIn>(optedIn: false, settings: null, DataSourceKey.DefaultName);

        context.Model.FindEntityType(typeof(FeatureFlagOverride)).Should().BeNull(
            "a host that never called AddReloadableFeatureFlags must build the model it had before");
    }

    [Fact]
    public void WithTheSettingsButNoOptIn_TheTableIsStillNotInTheModel()
    {
        using var context = CreateContext<CaseSettingsOnly>(
            optedIn: false, new FeatureFlagOverrideSettings(), DataSourceKey.DefaultName);

        context.Model.FindEntityType(typeof(FeatureFlagOverride)).Should().BeNull();
    }

    [Fact]
    public void WhenOptedInForANamedSource_OnlyThatSourcesContextGetsTheTable()
    {
        var settings = new FeatureFlagOverrideSettings { DataSourceName = ConferenceSource };

        using var conference = CreateContext<CaseNamedSource>(optedIn: true, settings, ConferenceSource);
        using var engagement = CreateContext<CaseNamedSource>(optedIn: true, settings, "Engagement");

        conference.Model.FindEntityType(typeof(FeatureFlagOverride))!.GetTableName()
            .Should().Be(FeatureFlagOverrideModelBuilderExtensions.TableName);
        engagement.Model.FindEntityType(typeof(FeatureFlagOverride)).Should().BeNull(
            "the overrides live in the owning service's own database only");
    }

    /// <summary>
    /// Mapped as an ordinary auditable entity: the soft-delete filter hides a cleared override and
    /// the row version is the concurrency token, as for any entity a module maps.
    /// </summary>
    [Fact]
    public void WhenOptedIn_TheEntityGetsTheSoftDeleteFilterAndTheConcurrencyToken()
    {
        using var context = CreateContext<CaseAuditableShape>(
            optedIn: true, new FeatureFlagOverrideSettings(), DataSourceKey.DefaultName);

        var entity = context.Model.FindEntityType(typeof(FeatureFlagOverride))!;

        entity.GetDeclaredQueryFilters().Should().Contain(filter => filter.Key == "SoftDelete");
        entity.FindProperty(nameof(FeatureFlagOverride.RowVersion))!.IsConcurrencyToken.Should().BeTrue();
        entity.FindPrimaryKey()!.Properties.Should().ContainSingle()
            .Which.GetColumnName().Should().Be("Name");
    }

    private static GateContext<TCase> CreateContext<TCase>(
        bool optedIn,
        FeatureFlagOverrideSettings? settings,
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
            services.AddSingleton<FeatureFlagOverrideModelGate>();
        }

        if (settings is not null)
        {
            services.AddSingleton<IOptions<FeatureFlagOverrideSettings>>(Options.Create(settings));
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

    private sealed class CaseNoOptIn;

    private sealed class CaseSettingsOnly;

    private sealed class CaseNamedSource;

    private sealed class CaseAuditableShape;

    [SuppressMessage(
        "Major Code Smell",
        "S2326:Unused type parameters should be removed",
        Justification = "Phantom on purpose: each closed type gets its own EF model cache entry.")]
    private sealed class GateContext<TCase>(
        DbContextOptions options,
        IServiceProvider serviceProvider,
        IEntityConfigurationAssemblyProvider assemblyProvider,
        PhysicalDataSource physicalDataSource)
        : ApplicationDbContext(options, serviceProvider, assemblyProvider, physicalDataSource)
    {
        /// <summary>Never dialled: model building needs a provider, not a server.</summary>
        internal const string ConnectionString = "Server=(local);Database=model-only;Trusted_Connection=True;";

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlServer(ConnectionString);
            base.OnConfiguring(optionsBuilder);
        }
    }
}

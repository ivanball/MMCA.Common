using System.Diagnostics.CodeAnalysis;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.ViewAs;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Infrastructure.Persistence.Auth;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Shared.Auth.Requests;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Auth.ViewAs;

/// <summary>
/// The <c>ViewAsGrants</c> table gate (ADR-137): <c>ViewAs:Enabled</c> AND <c>ViewAs:HostsGrantStore</c>
/// AND this context targeting <c>ViewAs:DataSourceName</c>. A service of a multi-service host that
/// enables view-as without holding the grant store must build the model it had before, or EF Migrate
/// fails at startup on a pending model change. Asserted against a real built model; one context type
/// per case for the model-cache reason
/// <see cref="Persistence.DbContexts.PermissionGrantModelGateTests"/> gives.
/// </summary>
public sealed class ViewAsGrantModelGateTests
{
    private const string IdentitySource = "Identity";

    [Fact]
    public void EnabledOnASingleHost_TheDefaultSourceGetsTheTable()
    {
        using var context = CreateContext<CaseSingleHost>(
            new ViewAsSettings { Enabled = true },
            DataSourceKey.DefaultName);

        context.Model.FindEntityType(typeof(ViewAsGrant)).Should().NotBeNull(
            "HostsGrantStore defaults to true, so a single host is unchanged");
    }

    [Fact]
    public void EnabledWithoutTheGrantStore_NoContextGetsTheTable()
    {
        var settings = new ViewAsSettings { Enabled = true, HostsGrantStore = false, DataSourceName = IdentitySource };

        using var identity = CreateContext<CaseNonStoreHost>(settings, IdentitySource);
        using var conference = CreateContext<CaseNonStoreHost>(settings, "Conference");

        identity.Model.FindEntityType(typeof(ViewAsGrant)).Should().BeNull(
            "a service that does not hold the grant store maps no ViewAsGrants table, even on a source with the configured name");
        conference.Model.FindEntityType(typeof(ViewAsGrant)).Should().BeNull();
    }

    [Fact]
    public void EnabledWithTheGrantStore_OnlyTheNamedSourceGetsTheTable()
    {
        var settings = new ViewAsSettings { Enabled = true, DataSourceName = IdentitySource };

        using var identity = CreateContext<CaseStoreHost>(settings, IdentitySource);
        using var conference = CreateContext<CaseStoreHost>(settings, "Conference");

        identity.Model.FindEntityType(typeof(ViewAsGrant))!.GetTableName()
            .Should().Be(ViewAsGrantModelBuilderExtensions.TableName);
        conference.Model.FindEntityType(typeof(ViewAsGrant)).Should().BeNull();
    }

    [Fact]
    public void Disabled_NoContextGetsTheTable()
    {
        using var context = CreateContext<CaseDisabled>(new ViewAsSettings(), DataSourceKey.DefaultName);

        context.Model.FindEntityType(typeof(ViewAsGrant)).Should().BeNull();
    }

    // The store stays registered on every host (the controller actions bind IViewAsService, which
    // needs it), so on a host without the grant store it must answer without touching a context.
    [Fact]
    public async Task EFViewAsGrantStore_OnAHostWithoutTheGrantStore_IsInert_AndNeverTouchesTheDatabase()
    {
        var factory = new Mock<IDbContextFactory>(MockBehavior.Strict);
        var sut = new EFViewAsGrantStore(
            factory.Object,
            Mock.Of<IEntityDataSourceRegistry>(),
            Mock.Of<IDataSourceResolver>(),
            Options.Create(new ViewAsSettings { Enabled = true, HostsGrantStore = false }));

        (await sut.FindAsync(Guid.NewGuid())).Should().BeNull();
        (await sut.EndAsync(Guid.NewGuid(), 1, DateTime.UtcNow)).Should().BeFalse();
        (await sut.GetRecentForTargetAsync(2, 10)).Should().BeEmpty();
        (await sut.DeleteIssuedBeforeAsync(DateTime.UtcNow)).Should().Be(0);
        var page = await sut.ListAsync(new ViewAsGrantListRequest(), DateTime.UtcNow);
        page.Items.Should().BeEmpty("the support access log reads as empty on a service without the store");
        page.TotalCount.Should().Be(0);
        (await sut.ForgetUserAsync(2)).Should().Be(0, "erasure on a service without the store has nothing to blank");
        factory.VerifyNoOtherCalls();
    }

    private static GateContext<TCase> CreateContext<TCase>(ViewAsSettings settings, string dataSourceName)
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
        services.AddSingleton<IOptions<ViewAsSettings>>(Options.Create(settings));

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

    private sealed class CaseSingleHost;

    private sealed class CaseNonStoreHost;

    private sealed class CaseStoreHost;

    private sealed class CaseDisabled;

    [SuppressMessage(
        "Major Code Smell",
        "S2326:Unused type parameters should be removed",
        Justification = "The parameter is deliberately phantom: its only job is to give each test case its own EF model cache entry.")]
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

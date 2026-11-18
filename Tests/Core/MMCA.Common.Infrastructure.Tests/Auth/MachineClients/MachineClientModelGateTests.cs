using System.Diagnostics.CodeAnalysis;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.MachineClients;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Auth.MachineClients;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Design;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.MachineClients;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Infrastructure.Tests.Persistence.DbContexts;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Auth.MachineClients;

/// <summary>
/// The <c>MachineClients</c> table gate (ADR-140, spec D "disabled means no table"): mapped only when
/// <c>MachineClients:Enabled</c> AND this context targets <c>MachineClients:DataSourceName</c>, the
/// passkey shape, so a host that never opts in builds the model it had before and needs no migration.
/// The client is auditable, so it is mapped before the soft-delete pass. The design-time flag
/// opens the same gate for <c>dotnet ef</c>. Asserted against real built models, one context type per
/// case for the EF model-cache reason the view-as gate tests give.
/// </summary>
public sealed class MachineClientModelGateTests : ModelGateTestsBase
{
    private const string IdentitySource = "Identity";

    protected override Type GatedEntityType => typeof(MachineClient);

    [Fact]
    public void Enabled_OnlyTheNamedSourceGetsTheTable()
    {
        var settings = new MachineClientSettings { Enabled = true, DataSourceName = IdentitySource };

        using var identity = CreateContext<CaseNamedSource>(settings, IdentitySource);
        using var conference = CreateContext<CaseNamedSource>(settings, "Conference");

        identity.Model.FindEntityType(typeof(MachineClient)).Should().NotBeNull();
        conference.Model.FindEntityType(typeof(MachineClient)).Should().BeNull();
    }

    // == Design time: the flag a consumer's migrations factory sets ==
    [Fact]
    public void DesignTime_WithEnableMachineClients_IncludesTheTable()
    {
        using var context = DesignTimeDbContextHelper.CreateSqlServer(
            ["--datasource", "DesignMachineClients"],
            options =>
            {
                ConfigureDesignTime(options);
                options.EnableMachineClients = true;
            });

        context.Model.FindEntityType(typeof(MachineClient)).Should().NotBeNull();
    }

    [Fact]
    public void DesignTime_ByDefault_ExcludesTheTable()
    {
        using var context = DesignTimeDbContextHelper.CreateSqlServer(["--datasource", "DesignNoMachineClients"], ConfigureDesignTime);

        context.Model.FindEntityType(typeof(MachineClient)).Should().BeNull(
            "a migrations project that never opts in must keep scaffolding exactly what it did before");
    }

    protected override DbContext CreateGateContext<TCase>(GateState state) => CreateContext<TCase>(
        state switch
        {
            GateState.NeverRegistered => null,
            GateState.Disabled => new MachineClientSettings(),
            GateState.Enabled => new MachineClientSettings { Enabled = true },
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
        },
        DataSourceKey.DefaultName);

    protected override void AssertEnabledMapping(IEntityType entity)
    {
        entity.GetTableName().Should().Be(MachineClientModelBuilderExtensions.TableName);
        entity.GetIndexes().Should().Contain(i => i.IsUnique && i.Properties.Single().Name == nameof(MachineClient.ServiceAccountUserId));
        entity.FindProperty(nameof(MachineClient.IsDeleted)).Should().NotBeNull("the client is an auditable entity, mapped before the soft-delete pass");
        entity.GetDeclaredQueryFilters().Should().Contain(
            filter => filter.Key == "SoftDelete", "mapped first, the soft-delete filter reaches it like any auditable entity");
    }

    private static void ConfigureDesignTime(DesignTimeDbContextOptions options)
    {
        options.ConnectionStrings = new ConnectionStringSettings { SQLServerConnectionString = "Server=design;Database=MachineClientsMain;" };

        // One physical source per case: EF caches a built model per (context type, source name).
        options.DataSources["DesignMachineClients"] = new DataSourceEntrySettings
        {
            SQLServerConnectionString = "Server=design;Database=MachineClients;",
            SQLServerMigrationsAssembly = "Design.MachineClients.Migrations",
        };
        options.DataSources["DesignNoMachineClients"] = new DataSourceEntrySettings
        {
            SQLServerConnectionString = "Server=design;Database=NoMachineClients;",
            SQLServerMigrationsAssembly = "Design.NoMachineClients.Migrations",
        };
    }

    private static GateContext<TCase> CreateContext<TCase>(MachineClientSettings? settings, string dataSourceName)
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AuditSaveChangesInterceptor>();
        services.AddSingleton(Mock.Of<IDomainEventDispatcher>());
        services.AddSingleton(Mock.Of<IOutboxSignal>());
        services.AddSingleton<ILogger<DomainEventSaveChangesInterceptor>>(NullLogger<DomainEventSaveChangesInterceptor>.Instance);
        services.AddSingleton<DomainEventSaveChangesInterceptor>();
        services.AddSingleton(Mock.Of<IEntityDataSourceRegistry>());
        if (settings is not null)
        {
            services.AddSingleton<IOptions<MachineClientSettings>>(Options.Create(settings));
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

    private sealed class CaseNamedSource;

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

using System.Diagnostics.CodeAnalysis;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.Passkeys;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Infrastructure.Persistence.Auth;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Design;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Auth.Passkeys;

/// <summary>
/// The <c>PasskeyCredentials</c> table gate (ADR-139): mapped only when <c>Passkeys:Enabled</c> AND this
/// context targets <c>Passkeys:DataSourceName</c>, the view-as grants shape, so a host that never opts
/// in (Store, Helpdesk) builds the model it had before and needs no migration. The design-time flag
/// opens the same gate for <c>dotnet ef</c>. Asserted against real built models, one context type per
/// case for the EF model-cache reason the view-as gate tests give.
/// </summary>
public sealed class PasskeyModelGateTests
{
    private const string IdentitySource = "Identity";

    [Fact]
    public void Disabled_NoContextGetsTheTable()
    {
        using var context = CreateContext<CaseDisabled>(new PasskeySettings(), DataSourceKey.DefaultName);

        context.Model.FindEntityType(typeof(PasskeyCredential)).Should().BeNull();
    }

    [Fact]
    public void NeverRegistered_NoContextGetsTheTable()
    {
        using var context = CreateContext<CaseNeverRegistered>(settings: null, DataSourceKey.DefaultName);

        context.Model.FindEntityType(typeof(PasskeyCredential)).Should().BeNull(
            "a host that never calls AddPasskeys registers no settings, and that absence reads as disabled");
    }

    [Fact]
    public void EnabledOnASingleHost_TheDefaultSourceGetsTheTable()
    {
        using var context = CreateContext<CaseSingleHost>(new PasskeySettings { Enabled = true }, DataSourceKey.DefaultName);

        var entity = context.Model.FindEntityType(typeof(PasskeyCredential));
        entity.Should().NotBeNull();
        entity!.GetTableName().Should().Be(PasskeyCredentialModelBuilderExtensions.TableName);
        entity.GetIndexes().Should().Contain(i => i.IsUnique && i.Properties.Single().Name == nameof(PasskeyCredential.CredentialId));
    }

    [Fact]
    public void Enabled_OnlyTheNamedSourceGetsTheTable()
    {
        var settings = new PasskeySettings { Enabled = true, DataSourceName = IdentitySource };

        using var identity = CreateContext<CaseNamedSource>(settings, IdentitySource);
        using var conference = CreateContext<CaseNamedSource>(settings, "Conference");

        identity.Model.FindEntityType(typeof(PasskeyCredential)).Should().NotBeNull();
        conference.Model.FindEntityType(typeof(PasskeyCredential)).Should().BeNull();
    }

    // == Design time: the flag a consumer's migrations factory sets ==
    [Fact]
    public void DesignTime_WithEnablePasskeys_IncludesTheTable()
    {
        using var context = DesignTimeDbContextHelper.CreateSqlServer(
            ["--datasource", "DesignPasskeys"],
            options =>
            {
                ConfigureDesignTime(options);
                options.EnablePasskeys = true;
            });

        context.Model.FindEntityType(typeof(PasskeyCredential)).Should().NotBeNull();
    }

    [Fact]
    public void DesignTime_ByDefault_ExcludesTheTable()
    {
        using var context = DesignTimeDbContextHelper.CreateSqlServer(["--datasource", "DesignNoPasskeys"], ConfigureDesignTime);

        context.Model.FindEntityType(typeof(PasskeyCredential)).Should().BeNull(
            "a migrations project that never opts in must keep scaffolding exactly what it did before");
    }

    private static void ConfigureDesignTime(DesignTimeDbContextOptions options)
    {
        options.ConnectionStrings = new ConnectionStringSettings { SQLServerConnectionString = "Server=design;Database=PasskeysMain;" };

        // One physical source per case: EF caches a built model per (context type, source name).
        options.DataSources["DesignPasskeys"] = new DataSourceEntrySettings
        {
            SQLServerConnectionString = "Server=design;Database=Passkeys;",
            SQLServerMigrationsAssembly = "Design.Passkeys.Migrations",
        };
        options.DataSources["DesignNoPasskeys"] = new DataSourceEntrySettings
        {
            SQLServerConnectionString = "Server=design;Database=NoPasskeys;",
            SQLServerMigrationsAssembly = "Design.NoPasskeys.Migrations",
        };
    }

    private static GateContext<TCase> CreateContext<TCase>(PasskeySettings? settings, string dataSourceName)
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
            services.AddSingleton<IOptions<PasskeySettings>>(Options.Create(settings));
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

    private sealed class CaseDisabled;

    private sealed class CaseNeverRegistered;

    private sealed class CaseSingleHost;

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

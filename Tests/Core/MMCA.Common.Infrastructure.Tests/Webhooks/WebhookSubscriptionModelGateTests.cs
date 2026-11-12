using System.Diagnostics.CodeAnalysis;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Webhooks;
using MMCA.Common.Domain.Webhooks;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Design;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Infrastructure.Persistence.Webhooks;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Webhooks;

/// <summary>
/// The <c>WebhookSubscriptions</c> table gate (design 35.7, 35.8 and 35(d)), the machine-client shape:
/// mapped only when <c>Webhooks:Enabled</c> AND this context targets <c>Webhooks:DataSourceName</c>, so
/// a host that never opts in builds the model it had before and needs no migration. The subscription
/// is auditable, so it is mapped before the soft-delete pass and gets the filter and the row version.
/// It is deliberately not a tenant entity (rule 35.12), so no tenant filter reaches it. The design-time
/// flag opens the same gate for <c>dotnet ef</c>. Asserted against real built models, one context type
/// per case for the EF model-cache reason <c>MachineClientModelGateTests</c> gives.
/// </summary>
public sealed class WebhookSubscriptionModelGateTests
{
    private const string IntegrationsSource = "Integrations";

    [Fact]
    public void Disabled_NoTable()
    {
        using var context = CreateContext<CaseDisabled>(new WebhookSettings(), DataSourceKey.DefaultName);

        context.Model.FindEntityType(typeof(WebhookSubscription)).Should().BeNull();
    }

    [Fact]
    public void NeverRegistered_NoTable()
    {
        using var context = CreateContext<CaseNeverRegistered>(settings: null, DataSourceKey.DefaultName);

        context.Model.FindEntityType(typeof(WebhookSubscription)).Should().BeNull(
            "a host that never calls AddWebhooks registers no settings, and that absence reads as disabled");
    }

    [Fact]
    public void Enabled_DefaultSourceGetsTableAndTenantIndex()
    {
        using var context = CreateContext<CaseSingleHost>(new WebhookSettings { Enabled = true }, DataSourceKey.DefaultName);

        var entity = context.Model.FindEntityType(typeof(WebhookSubscription));
        entity.Should().NotBeNull();
        entity!.GetTableName().Should().Be(WebhookSubscriptionModelBuilderExtensions.TableName);
        entity.GetTableName().Should().Be("WebhookSubscriptions");
        entity.GetSchema().Should().Be("dbo");
        var index = entity.GetIndexes().Should().ContainSingle(
            i => i.GetDatabaseName() == WebhookSubscriptionModelBuilderExtensions.TenantIndexName).Which;
        index.Properties.Select(p => p.Name).Should().Equal(nameof(WebhookSubscription.TenantId), nameof(WebhookSubscription.IsDeleted));
        index.IsUnique.Should().BeFalse("many subscriptions share a tenant");
        WebhookSubscriptionModelBuilderExtensions.TenantIndexName.Should().Be("IX_WebhookSubscriptions_TenantId_IsDeleted");
    }

    [Fact]
    public void Enabled_OtherSourceOnlyThere()
    {
        var settings = new WebhookSettings { Enabled = true, DataSourceName = IntegrationsSource };

        using var integrations = CreateContext<CaseNamedSource>(settings, IntegrationsSource);
        using var conference = CreateContext<CaseNamedSource>(settings, "Conference");
        using var fallback = CreateContext<CaseNamedSource>(settings, DataSourceKey.DefaultName);

        integrations.Model.FindEntityType(typeof(WebhookSubscription)).Should().NotBeNull();
        conference.Model.FindEntityType(typeof(WebhookSubscription)).Should().BeNull();
        fallback.Model.FindEntityType(typeof(WebhookSubscription)).Should().BeNull("only the named source carries the table");
    }

    [Fact]
    public void IsAuditable_SoftDeleteAndRowVersionMapped()
    {
        using var context = CreateContext<CaseAuditable>(new WebhookSettings { Enabled = true }, DataSourceKey.DefaultName);

        var entity = context.Model.FindEntityType(typeof(WebhookSubscription))!;
        entity.FindProperty(nameof(WebhookSubscription.IsDeleted)).Should().NotBeNull();
        entity.GetDeclaredQueryFilters().Should().Contain(
            filter => filter.Key == ApplicationDbContext.SoftDeleteFilterName,
            "mapped before ApplySoftDeleteFilters, the soft-delete filter reaches it like any auditable entity");
        var rowVersion = entity.FindProperty(nameof(WebhookSubscription.RowVersion));
        rowVersion.Should().NotBeNull();
        rowVersion!.IsConcurrencyToken.Should().BeTrue("RecordOutcomeAsync relies on a concurrency conflict being detectable");
        rowVersion.ValueGenerated.Should().Be(ValueGenerated.OnAddOrUpdate, "SQL Server generates the rowversion");
        entity.GetDeclaredQueryFilters().Should().NotContain(
            filter => filter.Key == ApplicationDbContext.TenantFilterName,
            "the subscription is not an ITenantEntity; the store filters by tenant itself (rule 35.12)");
    }

    [Fact]
    public void Columns_MatchTheDesign()
    {
        using var context = CreateContext<CaseColumns>(new WebhookSettings { Enabled = true }, DataSourceKey.DefaultName);
        var entity = context.Model.FindEntityType(typeof(WebhookSubscription))!;

        entity.FindPrimaryKey()!.Properties.Single().Name.Should().Be(nameof(WebhookSubscription.Id));
        entity.FindProperty(nameof(WebhookSubscription.Id))!.ValueGenerated.Should().Be(ValueGenerated.Never, "the id is never generated by the store");
        AssertColumn(entity, nameof(WebhookSubscription.Url), "nvarchar(2048)", nullable: false);
        AssertColumn(entity, nameof(WebhookSubscription.Description), "nvarchar(200)", nullable: true);
        AssertColumn(entity, nameof(WebhookSubscription.EventTypes), "varchar(2048)", nullable: false);
        AssertColumn(entity, nameof(WebhookSubscription.TenantId), "varchar(64)", nullable: true);
        AssertColumn(entity, nameof(WebhookSubscription.ProtectedSecret), "nvarchar(1024)", nullable: false);
        AssertColumn(entity, nameof(WebhookSubscription.PreviousProtectedSecret), "nvarchar(1024)", nullable: true);
        AssertColumn(entity, nameof(WebhookSubscription.LastFailure), "nvarchar(500)", nullable: true);
        entity.FindProperty(nameof(WebhookSubscription.PreviousSecretExpiresOn))!.IsNullable.Should().BeTrue();
        entity.FindProperty(nameof(WebhookSubscription.DisabledOn))!.IsNullable.Should().BeTrue();
        entity.FindProperty(nameof(WebhookSubscription.LastDeliveredOn))!.IsNullable.Should().BeTrue();
        entity.FindProperty(nameof(WebhookSubscription.LastFailedOn))!.IsNullable.Should().BeTrue();
    }

    // == Design time: the flag a consumer's migrations factory sets ==
    [Fact]
    public void DesignTimeFlag_MapsTable()
    {
        using var context = DesignTimeDbContextHelper.CreateSqlServer(
            ["--datasource", "DesignWebhooks"],
            options =>
            {
                ConfigureDesignTime(options);
                options.EnableWebhooks = true;
            });

        context.Model.FindEntityType(typeof(WebhookSubscription)).Should().NotBeNull();
    }

    [Fact]
    public void DesignTime_ByDefault_ExcludesTheTable()
    {
        using var context = DesignTimeDbContextHelper.CreateSqlServer(["--datasource", "DesignNoWebhooks"], ConfigureDesignTime);

        context.Model.FindEntityType(typeof(WebhookSubscription)).Should().BeNull(
            "a migrations project that never opts in must keep scaffolding exactly what it did before");
    }

    private static void AssertColumn(IEntityType entity, string propertyName, string storeType, bool nullable)
    {
        var property = entity.FindProperty(propertyName);
        property.Should().NotBeNull(propertyName);
        property!.GetRelationalTypeMapping().StoreType.Should().Be(storeType, propertyName);
        property.IsNullable.Should().Be(nullable, propertyName);
    }

    private static void ConfigureDesignTime(DesignTimeDbContextOptions options)
    {
        options.ConnectionStrings = new ConnectionStringSettings { SQLServerConnectionString = "Server=design;Database=WebhooksMain;" };

        // One physical source per case: EF caches a built model per (context type, source name).
        options.DataSources["DesignWebhooks"] = new DataSourceEntrySettings
        {
            SQLServerConnectionString = "Server=design;Database=Webhooks;",
            SQLServerMigrationsAssembly = "Design.Webhooks.Migrations",
        };
        options.DataSources["DesignNoWebhooks"] = new DataSourceEntrySettings
        {
            SQLServerConnectionString = "Server=design;Database=NoWebhooks;",
            SQLServerMigrationsAssembly = "Design.NoWebhooks.Migrations",
        };
    }

    private static GateContext<TCase> CreateContext<TCase>(WebhookSettings? settings, string dataSourceName)
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
            services.AddSingleton<IOptions<WebhookSettings>>(Options.Create(settings));
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

    private sealed class CaseAuditable;

    private sealed class CaseColumns;

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

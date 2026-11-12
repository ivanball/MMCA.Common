using System.Reflection;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.MachineClients;
using MMCA.Common.Application.Auth.Passkeys;
using MMCA.Common.Application.Auth.Permissions;
using MMCA.Common.Application.Auth.ViewAs;
using MMCA.Common.Application.FeatureFlags;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Webhooks;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Domain.Webhooks;
using MMCA.Common.Infrastructure.Persistence.AuditTrail;
using MMCA.Common.Infrastructure.Persistence.Auth;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Design;
using MMCA.Common.Infrastructure.Persistence.FeatureFlags;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Infrastructure.Scheduling;
using MMCA.Common.Infrastructure.Tests.Persistence.Conversions;
using MMCA.Common.Shared.Identifiers;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.DataSources;

/// <summary>
/// EF caches one built model per <see cref="DataSourceModelCacheKeyFactory"/> key, process-wide. The
/// gated tables make the model depend on more than (context type, source name): each of the nine
/// <c>_*TableEnabled</c> gates <see cref="ApplicationDbContext"/> resolves in <c>OnConfiguring</c>,
/// and the strongly typed identifier registry its conventions read. A key that leaves any of them out
/// hands a second, differently gated context the first context's model.
/// <para>
/// Unlike the per-gate tests, which give every case its own context type so the cache cannot couple
/// them, these cases deliberately build TWO contexts of one type against one source name, with EF's
/// service provider caching left on, so the second model comes out of the same cache the first went
/// into. Each case still owns its type, so the first-built model is the one the case chose.
/// </para>
/// </summary>
public sealed class ModelCacheKeyGateTests
{
    private const string Scheduler = "_schedulerTableEnabled";
    private const string AuditTrail = "_auditTrailTableEnabled";
    private const string RefreshSessions = "_refreshSessionTableEnabled";
    private const string PermissionGrants = "_permissionGrantTableEnabled";
    private const string ViewAsGrants = "_viewAsGrantTableEnabled";
    private const string FeatureFlagOverrides = "_featureFlagOverrideTableEnabled";
    private const string Passkeys = "_passkeyTableEnabled";
    private const string MachineClients = "_machineClientTableEnabled";
    private const string Webhooks = "_webhookSubscriptionTableEnabled";

    private static readonly string[] AllGates =
    [
        Scheduler, AuditTrail, RefreshSessions, PermissionGrants, ViewAsGrants,
        FeatureFlagOverrides, Passkeys, MachineClients, Webhooks,
    ];

    /// <summary>Gets every gate, one theory row each.</summary>
    public static TheoryData<string> Gates => new(AllGates);

    // == The model a second, differently gated context gets ==
    [Fact]
    public void Webhooks_OffThenOn_TheSecondModelMapsTheTable() =>
        AssertEachModelFollowsItsOwnGate<CaseWebhooksOffThenOn>(Webhooks, typeof(WebhookSubscription), firstOn: false);

    [Fact]
    public void Webhooks_OnThenOff_TheSecondModelDoesNotMapTheTable() =>
        AssertEachModelFollowsItsOwnGate<CaseWebhooksOnThenOff>(Webhooks, typeof(WebhookSubscription), firstOn: true);

    [Fact]
    public void AuditTrail_OffThenOn_TheSecondModelMapsTheTable() =>
        AssertEachModelFollowsItsOwnGate<CaseAuditTrailOffThenOn>(AuditTrail, typeof(AuditTrailEntry), firstOn: false);

    [Fact]
    public void AuditTrail_OnThenOff_TheSecondModelDoesNotMapTheTable() =>
        AssertEachModelFollowsItsOwnGate<CaseAuditTrailOnThenOff>(AuditTrail, typeof(AuditTrailEntry), firstOn: true);

    [Fact]
    public void PermissionGrants_OffThenOn_TheSecondModelMapsTheTable() =>
        AssertEachModelFollowsItsOwnGate<CasePermissionGrantsOffThenOn>(PermissionGrants, typeof(PermissionGrant), firstOn: false);

    [Fact]
    public void PermissionGrants_OnThenOff_TheSecondModelDoesNotMapTheTable() =>
        AssertEachModelFollowsItsOwnGate<CasePermissionGrantsOnThenOff>(PermissionGrants, typeof(PermissionGrant), firstOn: true);

    [Fact]
    public void Passkeys_OffThenOn_TheSecondModelMapsTheTable() =>
        AssertEachModelFollowsItsOwnGate<CasePasskeysOffThenOn>(Passkeys, typeof(PasskeyCredential), firstOn: false);

    [Fact]
    public void Passkeys_OnThenOff_TheSecondModelDoesNotMapTheTable() =>
        AssertEachModelFollowsItsOwnGate<CasePasskeysOnThenOff>(Passkeys, typeof(PasskeyCredential), firstOn: true);

    [Fact]
    public void DesignTime_SameSourceWithAndWithoutTheWebhookFlag_EachModelFollowsItsFlag()
    {
        // The shape a consumer's design-time model tests take: the sealed engine context, one source
        // name, the flag flipped between two helper calls.
        using (var off = DesignTimeDbContextHelper.CreateSqlServer(["--datasource", "KeyGateDesign"], ConfigureDesignTime))
        {
            off.Model.FindEntityType(typeof(WebhookSubscription)).Should().BeNull();
        }

        using var on = DesignTimeDbContextHelper.CreateSqlServer(
            ["--datasource", "KeyGateDesign"],
            options =>
            {
                ConfigureDesignTime(options);
                options.EnableWebhooks = true;
            });

        on.Model.FindEntityType(typeof(WebhookSubscription)).Should().NotBeNull(
            "a model built without the table must not be reused for a context that maps it");
    }

    // == The key itself ==
    [Fact]
    public void Key_IdenticalGates_IsEqual()
    {
        using var noneA = CreateContext<CaseKeys>([]);
        using var noneB = CreateContext<CaseKeys>([]);
        using var allA = CreateContext<CaseKeys>(AllGates);
        using var allB = CreateContext<CaseKeys>(AllGates);

        KeyOf(noneA).Should().Be(KeyOf(noneB), "two contexts with the same gates must still share one model");
        KeyOf(allA).Should().Be(KeyOf(allB), "two contexts with the same gates must still share one model");
    }

    [Theory]
    [MemberData(nameof(Gates))]
    public void Key_OneGateDiffers_IsDifferent(string gate)
    {
        using var none = CreateContext<CaseKeys>([]);
        using var onlyThis = CreateContext<CaseKeys>([gate]);
        using var all = CreateContext<CaseKeys>(AllGates);
        using var allButThis = CreateContext<CaseKeys>([.. AllGates.Where(g => g != gate)]);

        ReadGate(onlyThis, gate).Should().BeTrue("the registration must open exactly the gate under test");
        ReadGate(allButThis, gate).Should().BeFalse();

        KeyOf(onlyThis).Should().NotBe(KeyOf(none), $"{gate} changes the model, so it must change the key");
        KeyOf(allButThis).Should().NotBe(KeyOf(all), $"{gate} changes the model, so it must change the key");
    }

    [Fact]
    public void EveryTableGateField_IsCoveredByTheKeyTheory()
    {
        // A tenth gate added to the context without a row here would escape the theory above, and with
        // it the proof that the key reads it.
        var fields = typeof(ApplicationDbContext)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(f => f.FieldType == typeof(bool) && f.Name.EndsWith("TableEnabled", StringComparison.Ordinal))
            .Select(f => f.Name);

        fields.Should().BeEquivalentTo(AllGates);
    }

    [Fact]
    public void Key_TheSameIdentifierTypesInAnotherRegistry_IsEqual()
    {
        using var first = CreateContext<CaseKeys>([], new StronglyTypedIdRegistry([typeof(OrderId), typeof(CustomerId)]));
        using var second = CreateContext<CaseKeys>([], new StronglyTypedIdRegistry([typeof(CustomerId), typeof(OrderId)]));

        KeyOf(first).Should().Be(KeyOf(second), "two registries with the same content configure the same model");
    }

    [Fact]
    public void Key_DifferentIdentifierTypes_IsDifferent()
    {
        using var none = CreateContext<CaseKeys>([]);
        using var one = CreateContext<CaseKeys>([], new StronglyTypedIdRegistry([typeof(OrderId)]));
        using var two = CreateContext<CaseKeys>([], new StronglyTypedIdRegistry([typeof(OrderId), typeof(CustomerId)]));

        KeyOf(one).Should().NotBe(KeyOf(none), "each declared wrapper adds a conversion to the model");
        KeyOf(two).Should().NotBe(KeyOf(one), "each declared wrapper adds a conversion to the model");
    }

    private static void AssertEachModelFollowsItsOwnGate<TCase>(string gate, Type gatedEntity, bool firstOn)
    {
        using (var first = CreateContext<TCase>(firstOn ? [gate] : []))
        {
            (first.Model.FindEntityType(gatedEntity) is not null).Should().Be(firstOn);
        }

        using var second = CreateContext<TCase>(firstOn ? [] : [gate]);

        (second.Model.FindEntityType(gatedEntity) is not null).Should().Be(
            !firstOn,
            $"the second context resolved {gate} as {!firstOn}, and its model must follow its own gate, not the cached one");
    }

    private static object KeyOf(ApplicationDbContext context) =>
        context.GetService<IModelCacheKeyFactory>().Create(context, designTime: false);

    private static bool ReadGate(ApplicationDbContext context, string gate)
    {
        // Resolving any context service runs OnConfiguring, which is where the gates are set.
        _ = context.GetService<IModelCacheKeyFactory>();
        return (bool)typeof(ApplicationDbContext)
            .GetField(gate, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(context)!;
    }

    private static void ConfigureDesignTime(DesignTimeDbContextOptions options)
    {
        options.ConnectionStrings = new ConnectionStringSettings { SQLServerConnectionString = "Server=design;Database=KeyGateMain;" };
        options.DataSources["KeyGateDesign"] = new DataSourceEntrySettings
        {
            SQLServerConnectionString = "Server=design;Database=KeyGate;",
            SQLServerMigrationsAssembly = "Design.KeyGate.Migrations",
        };
    }

    private static KeyContext<TCase> CreateContext<TCase>(
        IReadOnlyCollection<string> openGates,
        StronglyTypedIdRegistry? identifiers = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AuditSaveChangesInterceptor>();
        services.AddSingleton(Mock.Of<IDomainEventDispatcher>());
        services.AddSingleton(Mock.Of<IOutboxSignal>());
        services.AddSingleton<ILogger<DomainEventSaveChangesInterceptor>>(NullLogger<DomainEventSaveChangesInterceptor>.Instance);
        services.AddSingleton<DomainEventSaveChangesInterceptor>();
        services.AddSingleton(Mock.Of<IEntityDataSourceRegistry>());
        if (identifiers is not null)
        {
            services.AddSingleton(identifiers);
        }

        foreach (var gate in openGates)
        {
            OpenGate(services, gate);
        }

        var assemblyProvider = new Mock<IEntityConfigurationAssemblyProvider>();
        assemblyProvider.Setup(x => x.GetConfigurationAssemblies()).Returns([]);

        return new KeyContext<TCase>(
            new DbContextOptionsBuilder<KeyContext<TCase>>().Options,
            services.BuildServiceProvider(),
            assemblyProvider.Object,
            new PhysicalDataSource(
                new DataSourceKey(DataSource.SQLServer, DataSourceKey.DefaultName),
                KeyContext<TCase>.ConnectionString,
                MigrationsAssembly: null,
                CosmosDatabaseName: string.Empty));
    }

    /// <summary>The real opt-in for each gate, on the Default source every case uses.</summary>
    private static void OpenGate(ServiceCollection services, string gate)
    {
        switch (gate)
        {
            case Scheduler:
                services.AddSingleton<IOptions<SchedulerSettings>>(Options.Create(new SchedulerSettings { Enabled = true }));
                break;
            case AuditTrail:
                services.AddSingleton<IOptions<AuditTrailSettings>>(Options.Create(new AuditTrailSettings { Enabled = true }));
                break;
            case RefreshSessions:
                services.AddSingleton<IOptions<RefreshSessionSettings>>(Options.Create(new RefreshSessionSettings { Enabled = true }));
                break;
            case PermissionGrants:
                services.AddSingleton<PermissionGrantModelGate>();
                services.AddSingleton<IOptions<PermissionGrantSettings>>(Options.Create(new PermissionGrantSettings()));
                break;
            case ViewAsGrants:
                services.AddSingleton<IOptions<ViewAsSettings>>(Options.Create(new ViewAsSettings { Enabled = true }));
                break;
            case FeatureFlagOverrides:
                services.AddSingleton<FeatureFlagOverrideModelGate>();
                services.AddSingleton<IOptions<FeatureFlagOverrideSettings>>(Options.Create(new FeatureFlagOverrideSettings()));
                break;
            case Passkeys:
                services.AddSingleton<IOptions<PasskeySettings>>(Options.Create(new PasskeySettings { Enabled = true }));
                break;
            case MachineClients:
                services.AddSingleton<IOptions<MachineClientSettings>>(Options.Create(new MachineClientSettings { Enabled = true }));
                break;
            case Webhooks:
                services.AddSingleton<IOptions<WebhookSettings>>(Options.Create(new WebhookSettings { Enabled = true }));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(gate), gate, "No registration opens this gate; add one.");
        }
    }

    private sealed class CaseWebhooksOffThenOn;

    private sealed class CaseWebhooksOnThenOff;

    private sealed class CaseAuditTrailOffThenOn;

    private sealed class CaseAuditTrailOnThenOff;

    private sealed class CasePermissionGrantsOffThenOn;

    private sealed class CasePermissionGrantsOnThenOff;

    private sealed class CasePasskeysOffThenOn;

    private sealed class CasePasskeysOnThenOff;

    private sealed class CaseKeys;

    /// <summary>A context type per case marker, so each case owns its EF model cache entries.</summary>
    private sealed class KeyContext<TCase>(
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
            // The application name carries the case marker; nothing reads it, but it is the one use
            // of the type parameter whose real job is giving each case its own context type.
            optionsBuilder.UseSqlServer(ConnectionString + "Application Name=" + typeof(TCase).Name + ";");
            base.OnConfiguring(optionsBuilder);
        }
    }
}

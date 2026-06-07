using System.Reflection;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.Infrastructure.Tests.Persistence.DataSources;

public sealed class EntityDataSourceRegistryTests
{
    private const string DefaultSql = "Server=localhost;Database=Main;";

    // ── [UseDatabase] override ──
    [Fact]
    public void GetDataSourceKey_UseDatabaseAttribute_WithDataSourcesEntry_ReturnsNamedKey()
    {
        var sut = CreateSut(new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
        {
            ["RegistryNamedDb"] = new() { SqliteConnectionString = "Data Source=named.db" },
        });

        var key = sut.GetDataSourceKey(typeof(RegistryOrder));

        key.Should().Be(new DataSourceKey(DataSource.Sqlite, "RegistryNamedDb"));
    }

    [Fact]
    public void GetDataSourceKey_UseDatabaseAttribute_WithoutEntry_CollapsesToDefault()
    {
        var sut = CreateSut();

        sut.GetDataSourceKey(typeof(RegistryOrder)).Should().Be(DataSourceKey.Default(DataSource.Sqlite));
    }

    // ── Fallback when neither attribute nor module namespace applies ──
    [Fact]
    public void GetDataSourceKey_NoAttributeAndNoDomainNamespace_ReturnsDefault()
    {
        var sut = CreateSut();

        sut.GetDataSourceKey(typeof(RegistryInvoice)).Should().Be(DataSourceKey.Default(DataSource.Sqlite));
    }

    // ── Namespace-derived module names (shared with SQL schema derivation) ──
    [Fact]
    public void NamespaceConventions_TypeWithDomainSegment_ReturnsPrecedingSegment() =>
        NamespaceConventions.GetModuleName(typeof(PushNotification)).Should().Be("Common");

    [Fact]
    public void NamespaceConventions_TypeWithoutDomainSegment_ReturnsNull() =>
        NamespaceConventions.GetModuleName(typeof(string)).Should().BeNull();

    // ── Engine comes from the configuration base class ──
    [Fact]
    public void GetDataSourceKey_SqlServerConfiguration_ReturnsSqlServerEngine()
    {
        var sut = CreateSut();

        sut.GetDataSourceKey(typeof(RegistrySqlServerEntity)).Engine.Should().Be(DataSource.SQLServer);
    }

    // ── Real framework configuration: [UseDatabase("Notification")] on Common's notification configs ──
    [Fact]
    public void GetDataSourceKey_PushNotification_WithNotificationEntry_RoutesToNotificationSource()
    {
        var sut = CreateSut(
            new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
            {
                ["Notification"] = new() { SQLServerConnectionString = "Server=localhost;Database=Notif;" },
            },
            assemblies: [typeof(DataSourceResolver).Assembly]);

        sut.GetDataSourceKey(typeof(PushNotification))
            .Should().Be(new DataSourceKey(DataSource.SQLServer, "Notification"));
    }

    [Fact]
    public void GetDataSourceKey_PushNotification_WithoutEntry_CollapsesToDefault()
    {
        var sut = CreateSut(assemblies: [typeof(DataSourceResolver).Assembly]);

        sut.GetDataSourceKey(typeof(PushNotification)).Should().Be(DataSourceKey.Default(DataSource.SQLServer));
    }

    // ── Duplicate registrations ──
    [Fact]
    public void Registry_DuplicateConfigs_AgreeingOnSource_DoesNotThrow()
    {
        // ConflictX/ConflictY have no DataSources entries, so both configurations collapse to
        // Default and agree.
        var sut = CreateSut();

        sut.GetDataSourceKey(typeof(RegistryDuplicate)).Should().Be(DataSourceKey.Default(DataSource.Sqlite));
    }

    [Fact]
    public void Registry_DuplicateConfigs_ConflictingSources_Throws()
    {
        var sut = CreateSut(new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
        {
            ["ConflictX"] = new() { SqliteConnectionString = "Data Source=x.db" },
            ["ConflictY"] = new() { SqliteConnectionString = "Data Source=y.db" },
        });

        var act = () => sut.GetDataSourceKey(typeof(RegistryDuplicate));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{nameof(RegistryDuplicate)}*")
            .WithMessage("*exactly one database*");
    }

    // ── Configurations without [UseDataSource] are skipped (legacy lenient behavior) ──
    [Fact]
    public void Registry_ConfigurationWithoutUseDataSourceAttribute_IsSkipped()
    {
        var sut = CreateSut();

        sut.TryGetDataSourceKey(typeof(RegistryUnattributed).FullName!, out _).Should().BeFalse();
    }

    // ── Unknown entities ──
    [Fact]
    public void GetDataSourceKey_UnknownEntity_Throws()
    {
        var sut = CreateSut();

        var act = () => sut.GetDataSourceKey("Does.Not.Exist");

        act.Should().Throw<InvalidOperationException>().WithMessage("*Does.Not.Exist*");
    }

    [Fact]
    public void TryGetDataSourceKey_UnknownEntity_ReturnsFalse()
    {
        var sut = CreateSut();

        sut.TryGetDataSourceKey("Does.Not.Exist", out _).Should().BeFalse();
    }

    // ── GetPhysicalSourcesInUse ──
    [Fact]
    public void GetPhysicalSourcesInUse_ContainsNamedAndDefaultSources()
    {
        var sut = CreateSut(new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
        {
            ["RegistryNamedDb"] = new() { SqliteConnectionString = "Data Source=named.db" },
        });

        var sources = sut.GetPhysicalSourcesInUse();

        sources.Should().Contain(new DataSourceKey(DataSource.Sqlite, "RegistryNamedDb"));
        sources.Should().Contain(DataSourceKey.Default(DataSource.Sqlite));
    }

    private static EntityDataSourceRegistry CreateSut(
        Dictionary<string, DataSourceEntrySettings>? sources = null,
        IReadOnlyList<Assembly>? assemblies = null)
    {
        var resolver = new DataSourceResolver(
            new ConnectionStringSettings { SQLServerConnectionString = DefaultSql },
            new DataSourcesSettings(sources),
            NullLogger<DataSourceResolver>.Instance);

        return new EntityDataSourceRegistry(
            new FixedAssemblyProvider(assemblies ?? [typeof(EntityDataSourceRegistryTests).Assembly]),
            resolver);
    }

    private sealed class FixedAssemblyProvider(IReadOnlyList<Assembly> assemblies) : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<Assembly> GetConfigurationAssemblies() => assemblies;
    }

    // ── Test entities & configurations ──
    public sealed class RegistryOrder : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class RegistryInvoice : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class RegistryDuplicate : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class RegistryUnattributed : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class RegistrySqlServerEntity : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    // NOTE: the configuration classes below are public (not private) because they are only
    // referenced via the registry's reflection scan — analyzers would flag unused private types.
    [UseDatabase("RegistryNamedDb")]
    public sealed class RegistryOrderConfiguration : EntityTypeConfigurationSqlite<RegistryOrder, int>;

    public sealed class RegistryInvoiceConfiguration : EntityTypeConfigurationSqlite<RegistryInvoice, int>;

    public sealed class RegistrySqlServerEntityConfiguration : EntityTypeConfigurationSQLServer<RegistrySqlServerEntity, int>;

    // Two configurations for the same entity. They only conflict when ConflictX/ConflictY are
    // mapped to distinct connections in a test's DataSources settings; with no entries both
    // collapse to Default and agree (keeping every other whole-assembly registry scan safe).
    [UseDatabase("ConflictX")]
    public sealed class RegistryDuplicateConfigurationA : EntityTypeConfigurationSqlite<RegistryDuplicate, int>;

    [UseDatabase("ConflictY")]
    public sealed class RegistryDuplicateConfigurationB : EntityTypeConfigurationSqlite<RegistryDuplicate, int>;

    /// <summary>Implements the provider interface directly (no [UseDataSource]) — must be skipped.</summary>
    public sealed class RegistryUnattributedConfiguration : IEntityTypeConfigurationSqlite<RegistryUnattributed, int>
    {
        public void Configure(EntityTypeBuilder<RegistryUnattributed> builder) => builder.HasKey(e => e.Id);
    }
}

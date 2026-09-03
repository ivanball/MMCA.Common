using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Infrastructure.Persistence.DataSources;

namespace MMCA.Common.Infrastructure.Tests.Settings;

/// <summary>
/// Coverage for the one startup rule over <see cref="ConnectionStringSettings"/>: a host must be
/// able to reach some database, declared either top level or as a named <c>DataSources</c> entry.
/// The rule spans both sections, which is why it is a validator rather than a <c>[Required]</c>
/// annotation on the SQL Server property.
/// </summary>
public sealed class ConnectionStringSettingsValidatorTests
{
    // ── The validator in isolation ──
    [Fact]
    public void Validate_AcceptsATopLevelSqlServerConnection() =>
        Validate(new ConnectionStringSettings { SQLServerConnectionString = "Server=test;Database=test" })
            .Failed.Should().BeFalse("every existing consumer configures exactly this and must keep booting");

    [Fact]
    public void Validate_AcceptsATopLevelSqliteConnection() =>
        Validate(new ConnectionStringSettings { SqliteConnectionString = "Data Source=app.db" })
            .Failed.Should().BeFalse();

    [Fact]
    public void Validate_AcceptsATopLevelCosmosConnection() =>
        Validate(new ConnectionStringSettings { CosmosConnectionString = "AccountEndpoint=https://test;AccountKey=dGVzdA==" })
            .Failed.Should().BeFalse();

    [Fact]
    public void Validate_AcceptsANamedSqliteSourceWithNoTopLevelConnection() =>
        Validate(
            new ConnectionStringSettings(),
            new DataSourcesSettings(new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
            {
                ["Tickets"] = new() { SqliteConnectionString = "Data Source=tickets.db" },
            }))
            .Failed.Should().BeFalse("a SQLite-only application declares its databases as named sources");

    [Fact]
    public void Validate_AcceptsANamedSqlServerSourceWithNoTopLevelConnection() =>
        Validate(
            new ConnectionStringSettings(),
            new DataSourcesSettings(new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
            {
                ["Conference"] = new() { SQLServerConnectionString = "Server=test;Database=Conference" },
            }))
            .Failed.Should().BeFalse("the resolver registers that entry as its own physical source");

    [Fact]
    public void Validate_RejectsAHostWithNoConnectionAnywhere()
    {
        var result = Validate(new ConnectionStringSettings());

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("No database connection is configured")
            .And.Contain("ConnectionStrings:SQLServerConnectionString")
            .And.Contain("DataSources");
    }

    [Fact]
    public void Validate_RejectsNamedSourcesThatCarryNoConnectionString()
    {
        var result = Validate(
            new ConnectionStringSettings(),
            new DataSourcesSettings(new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
            {
                // An entry that only names a migrations assembly collapses onto a Default source that
                // itself has no connection string, so the host still has nowhere to read or write.
                ["Tickets"] = new() { SqliteMigrationsAssembly = "App.Migrations.Sqlite" },
            }));

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithoutTheDataSourcesSection_StillChecksTheTopLevelSection() =>
        new ConnectionStringSettingsValidator().Validate(null, new ConnectionStringSettings())
            .Failed.Should().BeTrue();

    // ── Through AddInfrastructure's ValidateOnStart ──
    [Fact]
    public void ValidateOnStart_PassesForASqliteOnlyHost() =>
        StartupValidation(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["DataSources:Tickets:SqliteConnectionString"] = "Data Source=tickets.db",
            ["DataSources:Tickets:SqliteMigrationsAssembly"] = "App.Migrations.Sqlite",
        }).Should().NotThrow("a host with no SQL Server at all is a supported shape");

    [Fact]
    public void ValidateOnStart_PassesForTheClassicSqlServerHost() =>
        StartupValidation(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:SQLServerConnectionString"] = "Server=test;Database=test",
        }).Should().NotThrow();

    [Fact]
    public void ValidateOnStart_PassesForANamedSqlServerSourceOnly() =>
        StartupValidation(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["DataSources:Conference:SQLServerConnectionString"] = "Server=test;Database=Conference",
        }).Should().NotThrow();

    [Fact]
    public void ValidateOnStart_FailsWhenNoDatabaseIsConfiguredAnywhere() =>
        StartupValidation(new Dictionary<string, string?>(StringComparer.Ordinal))
            .Should().Throw<OptionsValidationException>()
            .WithMessage("*No database connection is configured*");

    private static ValidateOptionsResult Validate(
        ConnectionStringSettings settings,
        DataSourcesSettings? dataSources = null) =>
        new ConnectionStringSettingsValidator(dataSources ?? new DataSourcesSettings())
            .Validate(null, settings);

    /// <summary>
    /// Runs exactly what <c>ValidateOnStart</c> runs at host start, without spinning up a host:
    /// <see cref="IStartupValidator"/> is the service that call registers.
    /// </summary>
    private static Action StartupValidation(Dictionary<string, string?> configurationValues)
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build());

        var provider = services.BuildServiceProvider();
        return () =>
        {
            using (provider)
            {
                provider.GetRequiredService<IStartupValidator>().Validate();
            }
        };
    }
}

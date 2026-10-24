using System.Collections.Concurrent;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application;
using MMCA.Common.Infrastructure.Configuration;
using MMCA.Common.Infrastructure.Persistence.Tenancy;

namespace MMCA.Common.Infrastructure.Tests.Configuration;

/// <summary>
/// Unit tests for <see cref="StartupConfigurationSummaryService"/>: one Information line per host
/// start naming the version, engines, bus, cache and tenancy the host resolved, never a secret, plus
/// the Warning a RabbitMQ host with delayed redelivery off has been missing (rubric section 10).
/// </summary>
public sealed class StartupConfigurationSummaryServiceTests
{
    private const string SqlServerSecret = "Sql-S3cret-Pa55w0rd";
    private const string PostgreSqlSecret = "Pg-S3cret-Pa55w0rd";
    private const string BrokerSecret = "Amqp-S3cret-Pa55w0rd";

    private static readonly string SummaryCategory = typeof(StartupConfigurationSummaryService).FullName!;

    [Fact]
    public async Task HostStart_WithAddInfrastructureCalledTwice_WritesExactlyOneInformationSummary()
    {
        var recorder = new RecordingLoggerProvider();
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(BaseSettings());
        builder.Logging.AddProvider(recorder);

        builder.Services.AddApplication();

        // Two modules of one host each calling AddInfrastructure must still produce one line.
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddInfrastructure(builder.Configuration);

        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        recorder.Entries
            .Where(e => e.Category == SummaryCategory && e.Level == LogLevel.Information)
            .Should().ContainSingle("the summary is written once per host start");
    }

    [Fact]
    public async Task Summary_NamesTheVersionTheEnginesTheBusTheCacheAndTheTenancyMode()
    {
        var recorder = await StartSummaryAsync(
            ("MessageBus:Provider", "AzureServiceBus"),
            ("DataSources:Catalog:PostgreSQLConnectionString", $"Host=pg;Database=catalog;Password={PostgreSqlSecret}"),
            ("Tenancy:Enabled", "true"));

        var summary = recorder.Entries.Should()
            .ContainSingle(e => e.Category == SummaryCategory && e.Level == LogLevel.Information).Which.Message;

        StartupConfigurationSummaryService.FrameworkVersion.Should().NotBe("unknown", "MinVer stamps the informational version");
        summary.Should().Contain(StartupConfigurationSummaryService.FrameworkVersion);
        summary.Should().Contain("SQLServer").And.Contain("PostgreSQL");
        summary.Should().Contain("AzureServiceBus");
        summary.Should().Contain("EnableDelayedRedelivery=False");
        summary.Should().Contain("cache memory");
        summary.Should().Contain("tenancy shared-database");
    }

    [Fact]
    public async Task Summary_NeverLogsAConnectionStringOrAnyPartOfOne()
    {
        var recorder = await StartSummaryAsync(
            ("MessageBus:Provider", "RabbitMq"),
            ("MessageBus:ConnectionString", $"amqp://app:{BrokerSecret}@rabbit:5672"),
            ("DataSources:Catalog:PostgreSQLConnectionString", $"Host=pg;Database=catalog;Password={PostgreSqlSecret}"));

        recorder.Entries.Should().NotBeEmpty();
        foreach (var entry in recorder.Entries)
        {
            foreach (var secret in new[] { SqlServerSecret, PostgreSqlSecret, BrokerSecret, "Password=", "amqp://" })
            {
                entry.Message.Should().NotContain(secret, "a configuration summary must never carry a secret");
                entry.StateValues.Should().NotContain(
                    value => value.Contains(secret, StringComparison.Ordinal),
                    "the structured properties reach the log sink as well as the rendered message");
            }
        }
    }

    [Fact]
    public async Task RabbitMqWithDelayedRedeliveryOff_WarnsOnce()
    {
        var recorder = await StartSummaryAsync(("MessageBus:Provider", "RabbitMq"));

        var warning = recorder.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Which.Message;
        warning.Should().Contain("MessageBus:EnableDelayedRedelivery");
        warning.Should().Contain("rabbitmq_delayed_message_exchange", "the warning carries its own remedy");
    }

    [Fact]
    public async Task RabbitMqWithDelayedRedeliveryOn_DoesNotWarn()
    {
        var recorder = await StartSummaryAsync(
            ("MessageBus:Provider", "RabbitMq"),
            ("MessageBus:EnableDelayedRedelivery", "true"));

        recorder.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
        recorder.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information)
            .Which.Message.Should().Contain("EnableDelayedRedelivery=True");
    }

    [Theory]
    [InlineData("InProcess")]
    [InlineData("AzureServiceBus")]
    public async Task NonRabbitMqProvider_DoesNotWarn(string provider)
    {
        var recorder = await StartSummaryAsync(("MessageBus:Provider", provider));

        recorder.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void TenancyMode_NamesOffSharedAndDatabasePerTenant()
    {
        StartupConfigurationSummaryService.TenancyMode(Bind<TenancySettings>()).Should().Be("off");
        StartupConfigurationSummaryService.TenancyMode(Bind<TenancySettings>(("Enabled", "true")))
            .Should().Be("shared-database");
        StartupConfigurationSummaryService.TenancyMode(Bind<TenancySettings>(
                ("Enabled", "true"),
                ("Tenants:acme:DataSources:Default:SQLServerConnectionString", "Server=acme")))
            .Should().Be("database-per-tenant");
    }

    private static Dictionary<string, string?> BaseSettings() => new()
    {
        ["ConnectionStrings:SQLServerConnectionString"] = $"Server=tcp:db.example;Database=App;User Id=app;Password={SqlServerSecret}",
        ["InternalCommands:Enabled"] = "false",
    };

    private static T Bind<T>(params (string Key, string Value)[] settings)
        where T : new()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        return configuration.Get<T>() ?? new T();
    }

    /// <summary>
    /// Builds the registrations AddInfrastructure makes and starts the summary service alone, so a
    /// broker configuration does not also start an outbox processor against a database that does
    /// not exist.
    /// </summary>
    private static async Task<RecordingLoggerProvider> StartSummaryAsync(params (string Key, string Value)[] overrides)
    {
        var settings = BaseSettings();
        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var recorder = new RecordingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(recorder));
        services.AddInfrastructure(configuration);
        services.AddMultiTenancy(configuration);

        await using var provider = services.BuildServiceProvider();
        var sut = ActivatorUtilities.CreateInstance<StartupConfigurationSummaryService>(provider);
        await sut.StartAsync(TestContext.Current.CancellationToken);
        return recorder;
    }

    private sealed record LogEntry(string Category, LogLevel Level, string Message, IReadOnlyList<string> StateValues);

    /// <summary>
    /// Records every entry through a real logger factory, so the internal service's
    /// <c>ILogger&lt;T&gt;</c> is the framework's own (Castle cannot proxy it over an internal type).
    /// </summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries => [.. _entries];

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, _entries);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? pairs.Select(pair => pair.Value?.ToString() ?? string.Empty).ToList()
                    : [];
                entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception), values));
            }
        }
    }
}

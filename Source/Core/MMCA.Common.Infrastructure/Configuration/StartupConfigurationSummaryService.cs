using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Infrastructure.Caching;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.Tenancy;

namespace MMCA.Common.Infrastructure.Configuration;

/// <summary>
/// Writes one Information line at host start naming the posture the host actually resolved: the
/// MMCA.Common version, the database engines it configures, the message bus transport (with its
/// <c>EnableDelayedRedelivery</c> value), the cache mode and the tenancy mode. A support question
/// ("which version, which bus, which cache?") is then answered by the first lines of the log rather
/// than by reading the deployed configuration.
/// <para>
/// Only names, enum values and flags are logged. Connection strings, keys and every other secret
/// stay out: the engines are derived from WHETHER a connection string is set, never from its text.
/// </para>
/// <para>
/// Also emits the one Warning a RabbitMQ host with delayed redelivery off has been missing: without
/// it, a handler failure that outlasts the in-process retries dead-letters instead of being
/// rescheduled through the broker. Azure Service Bus applies redelivery unconditionally, so the
/// warning is RabbitMQ-only.
/// </para>
/// <para>
/// Registered by <c>AddInfrastructure</c> through <c>TryAddEnumerable</c>, so a host whose modules
/// each call it still writes the summary exactly once.
/// </para>
/// </summary>
/// <param name="connectionStrings">The top-level connection strings (read for presence only).</param>
/// <param name="dataSources">The named data source entries (read for presence only).</param>
/// <param name="messageBus">The bound message bus settings.</param>
/// <param name="tenancy">The bound tenancy settings; the defaults when the host never called <c>AddMultiTenancy</c>.</param>
/// <param name="cacheService">The cache the host resolved, whose implementation names the cache mode.</param>
/// <param name="logger">Logger for the summary and the warning.</param>
internal sealed partial class StartupConfigurationSummaryService(
    IOptions<ConnectionStringSettings> connectionStrings,
    DataSourcesSettings dataSources,
    IOptions<MessageBusSettings> messageBus,
    IOptions<TenancySettings> tenancy,
    ICacheService cacheService,
    ILogger<StartupConfigurationSummaryService> logger)
    : IHostedService
{
    /// <summary>
    /// Gets the MMCA.Common informational version MinVer stamped on this assembly, or
    /// <c>unknown</c> when the attribute is absent.
    /// </summary>
    internal static string FrameworkVersion { get; } =
        typeof(StartupConfigurationSummaryService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var bus = messageBus.Value;

        if (logger.IsEnabled(LogLevel.Information))
        {
            var engines = DataSourceResolver.ResolveConfiguredEngines(connectionStrings.Value, dataSources);
            var engineNames = engines.Count == 0 ? "none" : string.Join(", ", engines);
            var cacheMode = CacheMode(cacheService);
            var tenancyMode = TenancyMode(tenancy.Value);

            LogConfigurationSummary(
                logger,
                FrameworkVersion,
                engineNames,
                bus.Provider,
                bus.EnableDelayedRedelivery,
                cacheMode,
                tenancyMode);
        }

        if (bus.Provider == MessageBusProvider.RabbitMq && !bus.EnableDelayedRedelivery)
        {
            LogRabbitMqDelayedRedeliveryOff(logger, bus.RetryLimit);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Names the cache tier from the registered implementation.</summary>
    /// <param name="cache">The resolved cache service.</param>
    /// <returns><c>hybrid</c>, <c>distributed</c>, <c>memory</c>, or <c>custom</c> for a host's own implementation.</returns>
    internal static string CacheMode(ICacheService cache) => cache switch
    {
        HybridCacheService => "hybrid",
        DistributedCacheService => "distributed",
        MemoryCacheService => "memory",
        _ => "custom",
    };

    /// <summary>Names the tenancy mode from the bound settings.</summary>
    /// <param name="settings">The tenancy settings.</param>
    /// <returns>
    /// <c>off</c> when resolution is disabled, <c>database-per-tenant</c> when at least one tenant
    /// routes a source to its own database, and <c>shared-database</c> otherwise.
    /// </returns>
    internal static string TenancyMode(TenancySettings settings)
    {
        if (!settings.Enabled)
        {
            return "off";
        }

        return settings.Tenants.Values.Any(tenant => tenant.DataSources.Count > 0)
            ? "database-per-tenant"
            : "shared-database";
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "MMCA.Common {FrameworkVersion} configuration: data sources {DataSourceEngines}; message bus {MessageBusProvider} (EnableDelayedRedelivery={EnableDelayedRedelivery}); cache {CacheMode}; tenancy {TenancyMode}.")]
    private static partial void LogConfigurationSummary(
        ILogger logger,
        string frameworkVersion,
        string dataSourceEngines,
        MessageBusProvider messageBusProvider,
        bool enableDelayedRedelivery,
        string cacheMode,
        string tenancyMode);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "MessageBus:Provider is RabbitMq and MessageBus:EnableDelayedRedelivery is false, so broker-level delayed redelivery is OFF: a message whose handler still fails after the {RetryLimit} in-process retries is dead-lettered instead of being rescheduled through the broker, and an outage longer than the retry window loses the event to the error queue. Install the rabbitmq_delayed_message_exchange plugin on the broker and set MessageBus:EnableDelayedRedelivery=true. The Aspire development container does not ship the plugin, so a local stack is expected to show this warning.")]
    private static partial void LogRabbitMqDelayedRedeliveryOff(ILogger logger, int retryLimit);
}

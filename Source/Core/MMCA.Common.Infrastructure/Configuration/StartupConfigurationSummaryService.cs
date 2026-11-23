using System.Reflection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.Tenancy;

namespace MMCA.Common.Infrastructure.Configuration;

/// <summary>
/// Writes one Information line at host start naming the posture the host actually resolved: the
/// MMCA.Common version, the database engines it configures, the message bus transport (with its
/// <c>ProvisionTopology</c> value), the cache mode and the tenancy mode. A support question
/// ("which version, which bus, which cache?") is then answered by the first lines of the log rather
/// than by reading the deployed configuration.
/// <para>
/// Only names, enum values and flags are logged. Connection strings, keys and every other secret
/// stay out: the engines are derived from WHETHER a connection string is set, never from its text.
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
/// <param name="services">The root provider, asked which cache registrations exist; the cache itself is never resolved.</param>
/// <param name="logger">Logger for the summary and the warning.</param>
internal sealed partial class StartupConfigurationSummaryService(
    IOptions<ConnectionStringSettings> connectionStrings,
    DataSourcesSettings dataSources,
    IOptions<MessageBusSettings> messageBus,
    IOptions<TenancySettings> tenancy,
    IServiceProvider services,
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
            var cacheMode = CacheMode(services);
            var tenancyMode = TenancyMode(tenancy.Value);

            LogConfigurationSummary(
                logger,
                FrameworkVersion,
                engineNames,
                bus.Provider,
                bus.ProvisionTopology,
                cacheMode,
                tenancyMode);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Names the cache tier by repeating the decision the <c>ICacheService</c> registrations make,
    /// WITHOUT resolving <c>ICacheService</c>: the two-level cache resolves
    /// <c>IConnectionMultiplexer</c>, so constructing the cache here would open a Redis-backed host's
    /// connection at startup instead of on first use. <c>AddCommonHybridCache</c> registers
    /// <see cref="HybridCache"/> (checked by registration, not by resolving it). Without it, a
    /// resolved <see cref="IDistributedCache"/> that is not the in-memory one means the host wired
    /// Redis but not the cache over it, which the <c>AddCaching</c> factory refuses on first use; the
    /// summary names that state up front. Resolving <see cref="IDistributedCache"/> does not connect:
    /// the Redis implementation connects lazily.
    /// </summary>
    /// <param name="services">The root provider.</param>
    /// <returns><c>hybrid</c>, <c>memory</c>, or <c>misconfigured</c>.</returns>
    internal static string CacheMode(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.GetService<IServiceProviderIsService>()?.IsService(typeof(HybridCache)) == true)
        {
            return "hybrid";
        }

        return services.GetService<IDistributedCache>() is not null and not MemoryDistributedCache
            ? "misconfigured"
            : "memory";
    }

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
        Message = "MMCA.Common {FrameworkVersion} configuration: data sources {DataSourceEngines}; message bus {MessageBusProvider} (ProvisionTopology={ProvisionTopology}); cache {CacheMode}; tenancy {TenancyMode}.")]
    private static partial void LogConfigurationSummary(
        ILogger logger,
        string frameworkVersion,
        string dataSourceEngines,
        string messageBusProvider,
        bool provisionTopology,
        string cacheMode,
        string tenancyMode);
}

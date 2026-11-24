using Microsoft.Extensions.Configuration;
using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Infrastructure.Messaging.Transports;

/// <summary>
/// What <c>AddBrokerMessaging</c> hands an <see cref="IBrokerTransport"/>: the bound
/// <c>MessageBus</c> section, the connection string it resolved, the application configuration for any
/// transport-specific keys the shared settings do not carry, and the topology this host consumes.
/// </summary>
public sealed class BrokerTransportContext
{
    /// <summary>Initializes a new instance of the <see cref="BrokerTransportContext"/> class.</summary>
    /// <param name="settings">The bound <c>MessageBus</c> section.</param>
    /// <param name="connectionString">The resolved broker connection string, or <see langword="null"/>.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="topology">The queue and subscriptions this host consumes.</param>
    public BrokerTransportContext(
        MessageBusSettings settings,
        string? connectionString,
        IConfiguration configuration,
        BrokerTopology topology)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(topology);

        Settings = settings;
        ConnectionString = connectionString;
        Configuration = configuration;
        Topology = topology;
    }

    /// <summary>
    /// Gets the queue this host receives on and the topics bound to it. Also registered as a singleton,
    /// so a transport's hosted receiver can take it from DI.
    /// </summary>
    public BrokerTopology Topology { get; }

    /// <summary>Gets the bound <c>MessageBus</c> section.</summary>
    public MessageBusSettings Settings { get; }

    /// <summary>
    /// Gets the broker connection string, resolved in order from <c>MessageBus:ConnectionString</c>,
    /// <c>ConnectionStrings:rabbitmq</c> and <c>ConnectionStrings:messaging</c>, or
    /// <see langword="null"/> when none is set (the transport then keeps its own default host).
    /// </summary>
    public string? ConnectionString { get; }

    /// <summary>Gets the application configuration, for transport-specific keys.</summary>
    public IConfiguration Configuration { get; }
}

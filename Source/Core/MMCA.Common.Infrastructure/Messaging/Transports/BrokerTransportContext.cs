using Microsoft.Extensions.Configuration;

namespace MMCA.Common.Infrastructure.Messaging.Transports;

/// <summary>
/// What <c>AddBrokerMessaging</c> hands an <see cref="IBrokerTransport"/>: the bound
/// <c>MessageBus</c> section, the connection string it resolved, and the application configuration
/// for any transport-specific keys the shared settings do not carry.
/// </summary>
public sealed class BrokerTransportContext
{
    /// <summary>Initializes a new instance of the <see cref="BrokerTransportContext"/> class.</summary>
    /// <param name="settings">The bound <c>MessageBus</c> section.</param>
    /// <param name="connectionString">The resolved broker connection string, or <see langword="null"/>.</param>
    /// <param name="configuration">The application configuration.</param>
    public BrokerTransportContext(MessageBusSettings settings, string? connectionString, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configuration);

        Settings = settings;
        ConnectionString = connectionString;
        Configuration = configuration;
    }

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

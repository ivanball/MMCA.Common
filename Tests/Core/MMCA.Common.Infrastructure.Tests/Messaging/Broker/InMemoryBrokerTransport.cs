using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Infrastructure.Messaging.Transports;

namespace MMCA.Common.Infrastructure.Tests.Messaging.Broker;

/// <summary>
/// An in-memory <see cref="IBrokerTransport"/> for unit tests: <see cref="Register"/> records the
/// context it was handed and registers <see cref="Broker"/> as the <see cref="IBrokerPublisher"/>.
/// Nothing is received in the background; a test drives delivery explicitly through
/// <see cref="InMemoryBroker.DeliverAsync"/>, which applies the pipeline's dispositions the way a real
/// transport must.
/// </summary>
/// <param name="name">The provider name this transport is selected by.</param>
/// <param name="maxRedeliveryDelay">The redelivery cap the transport declares.</param>
internal sealed class InMemoryBrokerTransport(string name = InMemoryBrokerTransport.DefaultName, TimeSpan? maxRedeliveryDelay = null) : IBrokerTransport
{
    /// <summary>The default provider name.</summary>
    internal const string DefaultName = "InMemory";

    /// <summary>Gets the broker this transport publishes to.</summary>
    public InMemoryBroker Broker { get; } = new();

    /// <summary>Gets how many times <see cref="Register"/> ran.</summary>
    public int RegisterCalls { get; private set; }

    /// <summary>Gets the context of the last <see cref="Register"/> call.</summary>
    public BrokerTransportContext? Context { get; private set; }

    /// <inheritdoc />
    public string Name { get; } = name;

    /// <inheritdoc />
    public TimeSpan? MaxRedeliveryDelay { get; } = maxRedeliveryDelay;

    /// <inheritdoc />
    public void Register(IServiceCollection services, BrokerTransportContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);

        RegisterCalls++;
        Context = context;
        services.AddSingleton(Broker);
        services.AddSingleton<IBrokerPublisher>(Broker);
    }
}

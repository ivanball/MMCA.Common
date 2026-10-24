using MassTransit;

namespace MMCA.Common.Infrastructure.Messaging.Transports;

/// <summary>
/// One MassTransit broker transport, selectable by name through <c>MessageBus:Provider</c>.
/// <para>
/// This is the whole broker boundary. <c>AddBrokerMessaging</c> owns everything that is the same on
/// every broker (the endpoint name formatter, consumer registration, the outbox-only publish path, the
/// inbox), and hands the transport-specific part to the transport whose <see cref="Name"/> matches
/// the configured provider. RabbitMQ is built in; every other transport ships in an adapter package
/// (<c>MMCA.Common.Azure</c> for Azure Service Bus) with one registration method, so the core
/// packages never reference a vendor SDK and swapping the broker is a package reference, a
/// registration call and a configuration value.
/// </para>
/// <para>
/// Selection happens at REGISTRATION time, because MassTransit takes its transport inside the
/// <c>AddMassTransit</c> callback: an adapter registers its transport (through
/// <c>AddBrokerTransport</c>) before the host calls <c>AddBrokerMessaging</c>, and a provider name
/// with no registered transport fails that call with the package and the method to add.
/// </para>
/// </summary>
public interface IBrokerTransport
{
    /// <summary>
    /// The provider name a configuration file selects this transport by (<c>MessageBus:Provider</c>),
    /// compared case-insensitively. Stable and vendor-shaped, e.g. <c>AzureServiceBus</c>.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Selects and configures this transport on the bus being registered: one
    /// <c>Using{Transport}</c> call whose bus-factory callback sets the host, applies the shared
    /// receive policies (<see cref="BrokerTransportPolicies.ApplyReceivePolicies"/>) and ends with
    /// <c>ConfigureEndpoints(context)</c>.
    /// </summary>
    /// <param name="configurator">The MassTransit registration configurator, consumers already added.</param>
    /// <param name="context">The bound settings, the resolved connection string and the configuration.</param>
    void Configure(IBusRegistrationConfigurator configurator, BrokerTransportContext context);
}

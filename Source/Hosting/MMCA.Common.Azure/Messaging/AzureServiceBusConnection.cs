using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;

namespace MMCA.Common.Azure.Messaging;

/// <summary>
/// The process-wide Service Bus clients, one AMQP connection shared by the publisher and the
/// receiver. Registered as a singleton the container disposes on shutdown, which closes every
/// sender and processor created from it.
/// </summary>
internal sealed class AzureServiceBusConnection : IAsyncDisposable
{
    /// <summary>Initializes a new instance of the <see cref="AzureServiceBusConnection"/> class.</summary>
    /// <param name="endpoint">The resolved endpoint.</param>
    public AzureServiceBusConnection(AzureServiceBusEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        Endpoint = endpoint;
        Client = endpoint.CreateClient();
        Administration = endpoint.CreateAdministrationClient();
    }

    /// <summary>Gets the resolved endpoint.</summary>
    public AzureServiceBusEndpoint Endpoint { get; }

    /// <summary>Gets the data-plane client.</summary>
    public ServiceBusClient Client { get; }

    /// <summary>Gets the management-plane client.</summary>
    public ServiceBusAdministrationClient Administration { get; }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Client.DisposeAsync();
}

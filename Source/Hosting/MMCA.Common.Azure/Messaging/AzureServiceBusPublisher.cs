using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;
using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Azure.Messaging;

/// <summary>
/// The Azure Service Bus <see cref="IBrokerPublisher"/>: sends each message to its event's topic
/// through one cached <see cref="ServiceBusSender"/> per topic. When topology provisioning is on, a
/// missing topic is created before the first send to it (and remembered after). A send the broker
/// rejects throws, so the outbox retries the row.
/// </summary>
internal sealed class AzureServiceBusPublisher : IBrokerPublisher, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly AzureServiceBusTopologyProvisioner? _provisioner;
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="AzureServiceBusPublisher"/> class.</summary>
    /// <param name="client">The data-plane client.</param>
    /// <param name="provisioner">Creates a missing topic, or <see langword="null"/> when provisioning is off.</param>
    public AzureServiceBusPublisher(ServiceBusClient client, AzureServiceBusTopologyProvisioner? provisioner)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
        _provisioner = provisioner;
    }

    /// <inheritdoc />
    public async Task PublishAsync(OutgoingBrokerMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var topic = AzureServiceBusEntityNames.Topic(message.Topic);

        if (_provisioner is not null)
        {
            await _provisioner.EnsureTopicAsync(topic, cancellationToken).ConfigureAwait(false);
        }

        var sender = _senders.GetOrAdd(topic, static (path, client) => client.CreateSender(path), _client);

        await sender.SendMessageAsync(AzureServiceBusMessageMapping.ToServiceBusMessage(message), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var sender in _senders.Values)
        {
            await sender.DisposeAsync().ConfigureAwait(false);
        }

        _senders.Clear();
    }
}

using System.Collections.Concurrent;
using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Infrastructure.Messaging.Transports.RabbitMq;

/// <summary>
/// Creates the RabbitMQ entities the transport needs, once per process and idempotently, honoring
/// <see cref="MessageBusSettings.ProvisionTopology"/>. With provisioning on, every entity is declared
/// (a repeated declaration with the same arguments is a no-op on the broker). With it off, nothing is
/// declared: the host queue, its error queue and every delay queue the redelivery intervals need must
/// already exist, and the receive side checks the queue it is about to publish a copy to passively,
/// so a missing one fails the disposition (and the message is redelivered) instead of the copy being
/// silently dropped as unroutable.
/// </summary>
/// <param name="provisionTopology">Whether this host declares entities.</param>
internal sealed class RabbitMqProvisioner(bool provisionTopology)
{
    private readonly ConcurrentDictionary<string, byte> _exchanges = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _queues = new(StringComparer.Ordinal);

    /// <summary>Gets a value indicating whether this host declares entities.</summary>
    public bool ProvisionTopology { get; } = provisionTopology;

    /// <summary>
    /// Declares the host's receive topology: the queue, its error queue, and for every subscription the
    /// topic's fanout exchange and the binding from it to the queue.
    /// </summary>
    /// <param name="operations">The broker operations.</param>
    /// <param name="topology">The host topology.</param>
    /// <param name="cancellationToken">Cancels the declarations.</param>
    /// <returns>A task that completes when every entity exists.</returns>
    public async Task ProvisionReceiveAsync(IRabbitMqOperations operations, BrokerTopology topology, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(topology);

        if (!ProvisionTopology)
        {
            return;
        }

        var queue = RabbitMqNames.Queue(topology.QueueName);
        await operations.DeclareQueueAsync(queue, arguments: null, cancellationToken).ConfigureAwait(false);

        var errorQueue = RabbitMqNames.ErrorQueue(topology.QueueName);
        await operations.DeclareQueueAsync(errorQueue, arguments: null, cancellationToken).ConfigureAwait(false);
        _queues.TryAdd(errorQueue, 0);

        foreach (var subscription in topology.Subscriptions)
        {
            var exchange = RabbitMqNames.Exchange(subscription.Topic);
            await operations.DeclareFanoutExchangeAsync(exchange, cancellationToken).ConfigureAwait(false);
            _exchanges.TryAdd(exchange, 0);
            await operations.BindQueueAsync(queue, exchange, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Declares a topic's exchange before its first publish, when provisioning.</summary>
    /// <param name="operations">The broker operations.</param>
    /// <param name="exchange">The exchange name.</param>
    /// <param name="cancellationToken">Cancels the declaration.</param>
    /// <returns>A task that completes when the exchange exists (or immediately when not provisioning).</returns>
    public async Task EnsureExchangeAsync(IRabbitMqOperations operations, string exchange, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operations);

        if (!ProvisionTopology || _exchanges.ContainsKey(exchange))
        {
            return;
        }

        await operations.DeclareFanoutExchangeAsync(exchange, cancellationToken).ConfigureAwait(false);
        _exchanges.TryAdd(exchange, 0);
    }

    /// <summary>
    /// Forgets that an exchange was declared, after a publish to it failed, so the next publish declares
    /// it again (it may have been deleted out of band).
    /// </summary>
    /// <param name="exchange">The exchange name.</param>
    public void ForgetExchange(string exchange) => _exchanges.TryRemove(exchange, out _);

    /// <summary>Makes sure the host queue's error queue exists before a dead-lettered copy is published to it.</summary>
    /// <param name="operations">The broker operations.</param>
    /// <param name="queueName">The topology's queue name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The error queue name.</returns>
    public async Task<string> EnsureErrorQueueAsync(IRabbitMqOperations operations, string queueName, CancellationToken cancellationToken)
    {
        var errorQueue = RabbitMqNames.ErrorQueue(queueName);
        await EnsureQueueAsync(operations, errorQueue, arguments: null, cancellationToken).ConfigureAwait(false);
        return errorQueue;
    }

    /// <summary>
    /// Makes sure the delay queue for <paramref name="delaySeconds"/> exists, declaring it lazily the first
    /// time that interval is used.
    /// </summary>
    /// <param name="operations">The broker operations.</param>
    /// <param name="queueName">The topology's queue name.</param>
    /// <param name="delaySeconds">The interval in whole seconds.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The delay queue name.</returns>
    public async Task<string> EnsureDelayQueueAsync(IRabbitMqOperations operations, string queueName, long delaySeconds, CancellationToken cancellationToken)
    {
        var delayQueue = RabbitMqNames.DelayQueue(queueName, delaySeconds);
        await EnsureQueueAsync(operations, delayQueue, RabbitMqNames.DelayQueueArguments(queueName, delaySeconds), cancellationToken)
            .ConfigureAwait(false);
        return delayQueue;
    }

    private async Task EnsureQueueAsync(
        IRabbitMqOperations operations,
        string queue,
        Dictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operations);

        if (_queues.ContainsKey(queue))
        {
            return;
        }

        if (ProvisionTopology)
        {
            await operations.DeclareQueueAsync(queue, arguments, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await operations.EnsureQueueExistsAsync(queue, cancellationToken).ConfigureAwait(false);
        }

        _queues.TryAdd(queue, 0);
    }
}

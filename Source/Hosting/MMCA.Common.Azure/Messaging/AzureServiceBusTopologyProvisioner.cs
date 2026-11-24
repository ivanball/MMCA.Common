using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Azure.Messaging;

/// <summary>
/// Creates the Service Bus entities a host's <see cref="BrokerTopology"/> needs, idempotently, through
/// the management plane: the host's queue (dead-lettering expired messages into its native dead-letter
/// queue), a topic per consumed event, and on each topic one subscription named after the queue that
/// forwards every message (the default match-all rule) to it. The publisher also asks it for a missing
/// topic on its first publish. Entities that already exist are left exactly as they are, and a topic
/// once seen is cached, so a steady-state publish costs no management call.
/// </summary>
internal sealed partial class AzureServiceBusTopologyProvisioner
{
    private readonly ServiceBusAdministrationClient _administration;
    private readonly bool _isEmulator;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, bool> _knownTopics = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="AzureServiceBusTopologyProvisioner"/> class.</summary>
    /// <param name="administration">The management-plane client.</param>
    /// <param name="isEmulator">Whether entities must stay beneath the emulator's one-hour quota.</param>
    /// <param name="logger">The logger entity creation is reported to.</param>
    public AzureServiceBusTopologyProvisioner(ServiceBusAdministrationClient administration, bool isEmulator, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(administration);
        ArgumentNullException.ThrowIfNull(logger);

        _administration = administration;
        _isEmulator = isEmulator;
        _logger = logger;
    }

    /// <summary>
    /// Creates the queue, then each topic and its forwarding subscription. A topology with no
    /// subscriptions creates nothing: a host that only publishes owns no queue.
    /// </summary>
    /// <param name="topology">The host's topology.</param>
    /// <param name="cancellationToken">Cancels provisioning.</param>
    /// <returns>A task that completes when every entity exists.</returns>
    public async Task ProvisionAsync(BrokerTopology topology, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topology);

        if (topology.Subscriptions.Count == 0)
        {
            return;
        }

        var queue = AzureServiceBusEntityNames.Queue(topology.QueueName);
        var subscription = AzureServiceBusEntityNames.Subscription(topology.QueueName);

        // The queue first: a subscription cannot forward to an entity that does not exist yet.
        await EnsureQueueAsync(queue, cancellationToken).ConfigureAwait(false);

        foreach (var topic in topology.Subscriptions.Select(s => AzureServiceBusEntityNames.Topic(s.Topic)).Distinct(StringComparer.Ordinal))
        {
            await EnsureTopicAsync(topic, cancellationToken).ConfigureAwait(false);
            await EnsureSubscriptionAsync(topic, subscription, queue, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Creates <paramref name="topic"/> unless it exists, then remembers it for the life of the process.</summary>
    /// <param name="topic">The topic path.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the topic exists.</returns>
    public async Task EnsureTopicAsync(string topic, CancellationToken cancellationToken)
    {
        if (_knownTopics.ContainsKey(topic))
        {
            return;
        }

        var exists = await _administration.TopicExistsAsync(topic, cancellationToken).ConfigureAwait(false);
        if (!exists.Value
            && await CreateUnlessExistsAsync(() => _administration.CreateTopicAsync(BuildTopicOptions(topic, _isEmulator), cancellationToken)).ConfigureAwait(false))
        {
            LogEntityCreated(_logger, "topic", topic);
        }

        _knownTopics.TryAdd(topic, true);
    }

    /// <summary>The options a topic is created with.</summary>
    /// <param name="topic">The topic path.</param>
    /// <param name="isEmulator">Whether the emulator's one-hour time-to-live ceiling applies.</param>
    /// <returns>The creation options.</returns>
    internal static CreateTopicOptions BuildTopicOptions(string topic, bool isEmulator)
    {
        var options = new CreateTopicOptions(topic);
        if (isEmulator)
        {
            options.DefaultMessageTimeToLive = ServiceBusEmulatorSupport.EmulatorEntityQuota;
        }

        return options;
    }

    /// <summary>
    /// The options the host's queue is created with: an expired message moves to the native
    /// dead-letter queue instead of vanishing, and duplicate detection stays off, because a scheduled
    /// redelivery deliberately reuses the original message id.
    /// </summary>
    /// <param name="queue">The queue path.</param>
    /// <param name="isEmulator">Whether the emulator's one-hour time-to-live ceiling applies.</param>
    /// <returns>The creation options.</returns>
    internal static CreateQueueOptions BuildQueueOptions(string queue, bool isEmulator)
    {
        var options = new CreateQueueOptions(queue)
        {
            DeadLetteringOnMessageExpiration = true,
            RequiresDuplicateDetection = false,
        };
        if (isEmulator)
        {
            options.DefaultMessageTimeToLive = ServiceBusEmulatorSupport.EmulatorEntityQuota;
        }

        return options;
    }

    /// <summary>
    /// The options a topic-to-queue subscription is created with: named after the queue, forwarding
    /// every message to it. The service adds the default match-all rule when none is given.
    /// </summary>
    /// <param name="topic">The topic path.</param>
    /// <param name="subscription">The subscription name.</param>
    /// <param name="forwardTo">The queue path messages are forwarded to.</param>
    /// <param name="isEmulator">Whether the emulator's one-hour time-to-live ceiling applies.</param>
    /// <returns>The creation options.</returns>
    internal static CreateSubscriptionOptions BuildSubscriptionOptions(string topic, string subscription, string forwardTo, bool isEmulator)
    {
        var options = new CreateSubscriptionOptions(topic, subscription) { ForwardTo = forwardTo };
        if (isEmulator)
        {
            options.DefaultMessageTimeToLive = ServiceBusEmulatorSupport.EmulatorEntityQuota;
        }

        return options;
    }

    private async Task EnsureQueueAsync(string queue, CancellationToken cancellationToken)
    {
        var exists = await _administration.QueueExistsAsync(queue, cancellationToken).ConfigureAwait(false);
        if (!exists.Value
            && await CreateUnlessExistsAsync(() => _administration.CreateQueueAsync(BuildQueueOptions(queue, _isEmulator), cancellationToken)).ConfigureAwait(false))
        {
            LogEntityCreated(_logger, "queue", queue);
        }
    }

    private async Task EnsureSubscriptionAsync(string topic, string subscription, string forwardTo, CancellationToken cancellationToken)
    {
        var exists = await _administration.SubscriptionExistsAsync(topic, subscription, cancellationToken).ConfigureAwait(false);
        if (!exists.Value
            && await CreateUnlessExistsAsync(() => _administration.CreateSubscriptionAsync(BuildSubscriptionOptions(topic, subscription, forwardTo, _isEmulator), cancellationToken)).ConfigureAwait(false))
        {
            LogSubscriptionCreated(_logger, topic, subscription, forwardTo);
        }
    }

    /// <summary>
    /// Runs a create call and absorbs the conflict of another host (or another first publish) having
    /// created the same entity in between the existence check and the call.
    /// </summary>
    /// <returns><see langword="true"/> when this call created the entity.</returns>
    private static async Task<bool> CreateUnlessExistsAsync(Func<Task> create)
    {
        try
        {
            await create().ConfigureAwait(false);
            return true;
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityAlreadyExists)
        {
            return false;
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Created the Azure Service Bus {EntityKind} '{EntityPath}'.")]
    private static partial void LogEntityCreated(ILogger logger, string entityKind, string entityPath);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Created the Azure Service Bus subscription '{Subscription}' on topic '{Topic}', forwarding to queue '{Queue}'.")]
    private static partial void LogSubscriptionCreated(ILogger logger, string topic, string subscription, string queue);
}

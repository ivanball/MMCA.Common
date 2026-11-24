using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Azure.Messaging;

/// <summary>
/// The Service Bus entity names the transport derives from the framework's broker names. Azure
/// Service Bus caps a queue or topic path at 260 characters and a subscription name at 50, so each
/// name goes through <see cref="BrokerNaming.Fit"/>: unchanged when it fits, otherwise shortened
/// deterministically so the publisher and every consumer compute the same entity.
/// </summary>
internal static class AzureServiceBusEntityNames
{
    /// <summary>The longest queue or topic path Service Bus accepts.</summary>
    internal const int MaxEntityPathLength = 260;

    /// <summary>The longest subscription name Service Bus accepts.</summary>
    internal const int MaxSubscriptionNameLength = 50;

    /// <summary>The host's queue: <c>Fit(queueName, 260)</c>.</summary>
    /// <param name="queueName">The topology's queue name.</param>
    /// <returns>The queue path.</returns>
    internal static string Queue(string queueName) => BrokerNaming.Fit(queueName, MaxEntityPathLength);

    /// <summary>An event's topic: <c>Fit(topic, 260)</c>.</summary>
    /// <param name="topic">The framework topic name (<c>{namespace}-{event}</c>).</param>
    /// <returns>The topic path.</returns>
    internal static string Topic(string topic) => BrokerNaming.Fit(topic, MaxEntityPathLength);

    /// <summary>
    /// The subscription that binds a topic to the host's queue: named after the queue,
    /// <c>Fit(queueName, 50)</c>, so each consuming host owns exactly one subscription per topic.
    /// </summary>
    /// <param name="queueName">The topology's queue name.</param>
    /// <returns>The subscription name.</returns>
    internal static string Subscription(string queueName) => BrokerNaming.Fit(queueName, MaxSubscriptionNameLength);
}

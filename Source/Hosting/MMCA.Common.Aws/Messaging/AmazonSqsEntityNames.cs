using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Aws.Messaging;

/// <summary>
/// The SNS topic and SQS queue names the transport derives from the framework's broker names. SNS caps
/// a topic name at 256 characters and SQS a queue name at 80, so each name goes through
/// <see cref="BrokerNaming.Fit"/>: unchanged when it fits, otherwise shortened deterministically so the
/// publisher and every consumer compute the same entity. The framework's names are lower kebab
/// (<c>[a-z0-9-]</c>), which both services accept as is.
/// </summary>
internal static class AmazonSqsEntityNames
{
    /// <summary>The longest SNS topic name.</summary>
    internal const int MaxTopicNameLength = 256;

    /// <summary>The longest SQS queue name.</summary>
    internal const int MaxQueueNameLength = 80;

    /// <summary>The suffix of the queue dead-lettered messages move to.</summary>
    internal const string ErrorQueueSuffix = "-error";

    /// <summary>An event's SNS topic: <c>Fit(topic, 256)</c>.</summary>
    /// <param name="topic">The framework topic name (<c>{namespace}-{event}</c>).</param>
    /// <returns>The topic name.</returns>
    internal static string Topic(string topic) => BrokerNaming.Fit(topic, MaxTopicNameLength);

    /// <summary>The host's SQS queue: <c>Fit(queueName, 80)</c>.</summary>
    /// <param name="queueName">The topology's queue name.</param>
    /// <returns>The queue name.</returns>
    internal static string Queue(string queueName) => BrokerNaming.Fit(queueName, MaxQueueNameLength);

    /// <summary>The host's dead-letter queue: <c>Fit(queueName + "-error", 80)</c>.</summary>
    /// <param name="queueName">The topology's queue name.</param>
    /// <returns>The error queue name.</returns>
    internal static string ErrorQueue(string queueName) => BrokerNaming.Fit(queueName + ErrorQueueSuffix, MaxQueueNameLength);
}

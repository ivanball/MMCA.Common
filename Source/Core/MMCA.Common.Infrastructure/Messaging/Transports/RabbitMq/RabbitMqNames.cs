using System.Globalization;
using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Infrastructure.Messaging.Transports.RabbitMq;

/// <summary>
/// The RabbitMQ entity names and arguments the transport derives from the broker topology. Every name
/// passes through <see cref="BrokerNaming.Fit"/> with RabbitMQ's 255-character limit, so a publisher and
/// a consumer in different hosts always compute the same entity.
/// <list type="bullet">
///   <item>Exchange: one durable fanout exchange per topic, named after the topic.</item>
///   <item>Queue: the host's durable queue, named after <see cref="BrokerTopology.QueueName"/>.</item>
///   <item>Error queue: <c>{queue}-error</c>, where dead-lettered messages land.</item>
///   <item>Delay queue: <c>{queue}-delay-{seconds}s</c>, one per redelivery interval, whose
///   <c>x-message-ttl</c> dead-letters each message back to the host queue through the default
///   exchange once the delay has passed (no plugin).</item>
/// </list>
/// </summary>
internal static class RabbitMqNames
{
    /// <summary>RabbitMQ's limit on exchange and queue names (a short string, 255 bytes).</summary>
    internal const int MaxNameLength = 255;

    /// <summary>The header carrying the dead-letter reason on a message in the error queue.</summary>
    internal const string DeadLetterReasonHeader = "x-mmca-dead-letter-reason";

    /// <summary>The header carrying the dead-letter detail on a message in the error queue.</summary>
    internal const string DeadLetterDetailHeader = "x-mmca-dead-letter-detail";

    /// <summary>The fanout exchange a topic is published to.</summary>
    /// <param name="topic">The topic (<see cref="BrokerNaming.Topic"/>).</param>
    /// <returns>The exchange name.</returns>
    internal static string Exchange(string topic) => BrokerNaming.Fit(topic, MaxNameLength);

    /// <summary>The host's durable receive queue.</summary>
    /// <param name="queueName">The topology's queue name.</param>
    /// <returns>The queue name.</returns>
    internal static string Queue(string queueName) => BrokerNaming.Fit(queueName, MaxNameLength);

    /// <summary>The host queue's error (dead-letter) queue: <c>{queue}-error</c>.</summary>
    /// <param name="queueName">The topology's queue name.</param>
    /// <returns>The error queue name.</returns>
    internal static string ErrorQueue(string queueName) => BrokerNaming.Fit(queueName + "-error", MaxNameLength);

    /// <summary>
    /// The whole seconds a redelivery delay waits for: rounded up, and at least one, so a sub-second
    /// delay still goes through a delay queue rather than straight back to the host queue.
    /// </summary>
    /// <param name="delay">The requested delay.</param>
    /// <returns>The delay in whole seconds.</returns>
    internal static long DelaySeconds(TimeSpan delay) => Math.Max(1L, (long)Math.Ceiling(delay.TotalSeconds));

    /// <summary>The delay queue for one redelivery interval: <c>{queue}-delay-{seconds}s</c>.</summary>
    /// <param name="queueName">The topology's queue name.</param>
    /// <param name="delaySeconds">The interval, in whole seconds (<see cref="DelaySeconds"/>).</param>
    /// <returns>The delay queue name.</returns>
    internal static string DelayQueue(string queueName, long delaySeconds) =>
        BrokerNaming.Fit(queueName + "-delay-" + delaySeconds.ToString(CultureInfo.InvariantCulture) + "s", MaxNameLength);

    /// <summary>
    /// The arguments a delay queue is declared with: every message expires after the interval and is
    /// then dead-lettered through the default exchange (<c>""</c>) to the host queue. Every message in one
    /// delay queue has the same time to live, so expiry at the head of the queue keeps them in order.
    /// The arguments are a pure function of the name, so every host declares the queue identically and a
    /// repeated declaration is a no-op.
    /// </summary>
    /// <param name="queueName">The topology's queue name (the dead-letter target is its fitted name).</param>
    /// <param name="delaySeconds">The interval, in whole seconds.</param>
    /// <returns>The declaration arguments.</returns>
    internal static Dictionary<string, object?> DelayQueueArguments(string queueName, long delaySeconds) => new(StringComparer.Ordinal)
    {
        ["x-message-ttl"] = delaySeconds * 1000L,
        ["x-dead-letter-exchange"] = string.Empty,
        ["x-dead-letter-routing-key"] = Queue(queueName),
    };
}

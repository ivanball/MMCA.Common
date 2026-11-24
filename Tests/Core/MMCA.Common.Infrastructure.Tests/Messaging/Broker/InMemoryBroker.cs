using System.Collections.Concurrent;
using System.Globalization;
using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Infrastructure.Tests.Messaging.Broker;

/// <summary>
/// The in-memory broker behind <see cref="InMemoryBrokerTransport"/>: records every publish, and
/// delivers a message through an <see cref="IBrokerReceivePipeline"/> while applying the dispositions
/// (a redelivery re-enters at once with the redelivery-count header bumped; the delay is recorded, not
/// waited).
/// </summary>
internal sealed class InMemoryBroker : IBrokerPublisher
{
    private readonly ConcurrentQueue<OutgoingBrokerMessage> _published = new();

    /// <summary>Gets every message published, in order.</summary>
    public IReadOnlyList<OutgoingBrokerMessage> Published => [.. _published];

    /// <inheritdoc />
    public Task PublishAsync(OutgoingBrokerMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        _published.Enqueue(message);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Delivers <paramref name="message"/> to <paramref name="pipeline"/> and keeps applying
    /// <see cref="BrokerDisposition.Redeliver"/> until the pipeline completes or dead-letters it.
    /// </summary>
    /// <param name="pipeline">The receive pipeline.</param>
    /// <param name="message">The published message to deliver.</param>
    /// <param name="cancellationToken">Forwarded to the pipeline.</param>
    /// <returns>Every disposition the pipeline returned, in order.</returns>
    public static async Task<IReadOnlyList<BrokerDisposition>> DeliverAsync(
        IBrokerReceivePipeline pipeline,
        OutgoingBrokerMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(message);

        List<BrokerDisposition> dispositions = [];
        var headers = new Dictionary<string, string>(message.Headers, StringComparer.Ordinal);

        while (true)
        {
            var disposition = await pipeline.ProcessAsync(
                new IncomingBrokerMessage(message.Body, headers, "in-memory-" + dispositions.Count.ToString(CultureInfo.InvariantCulture)),
                cancellationToken);
            dispositions.Add(disposition);

            if (disposition is not BrokerDisposition.Redeliver redeliver)
            {
                return dispositions;
            }

            headers = new Dictionary<string, string>(headers, StringComparer.Ordinal)
            {
                [BrokerHeaders.RedeliveryCount] = redeliver.RedeliveryCount.ToString(CultureInfo.InvariantCulture),
            };
        }
    }
}

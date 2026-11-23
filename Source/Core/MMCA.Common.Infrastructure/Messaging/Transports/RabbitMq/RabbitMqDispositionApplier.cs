using System.Globalization;
using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Infrastructure.Messaging.Transports.RabbitMq;

/// <summary>
/// Carries out a <see cref="BrokerDisposition"/> for a message delivered on the host queue, up to (not
/// including) the acknowledgement of the original, which the receiver sends once this returns:
/// <list type="bullet">
///   <item><see cref="BrokerDisposition.Completed"/>: nothing to publish.</item>
///   <item><see cref="BrokerDisposition.Redeliver"/>: a confirmed copy with the new
///   <see cref="BrokerHeaders.RedeliveryCount"/> goes to the delay queue for the interval, which
///   dead-letters it back to the host queue when the interval has passed.</item>
///   <item><see cref="BrokerDisposition.DeadLetter"/>: a confirmed copy with
///   <see cref="RabbitMqNames.DeadLetterReasonHeader"/> and <see cref="RabbitMqNames.DeadLetterDetailHeader"/>
///   goes to <c>{queue}-error</c>.</item>
/// </list>
/// Both copies are published mandatory and confirmed, so the original is acknowledged only once its copy
/// is safely in a queue; a failure here throws and the receiver returns the original to the queue.
/// </summary>
internal static class RabbitMqDispositionApplier
{
    /// <summary>Publishes whatever copy <paramref name="disposition"/> calls for.</summary>
    /// <param name="operations">The broker operations to publish with.</param>
    /// <param name="provisioner">Declares (or checks) the delay and error queues.</param>
    /// <param name="queueName">The topology's queue name.</param>
    /// <param name="message">The delivered message.</param>
    /// <param name="disposition">The pipeline's decision.</param>
    /// <param name="cancellationToken">Cancels the publish.</param>
    /// <returns>A task that completes when the original may be acknowledged.</returns>
    internal static async Task ApplyAsync(
        IRabbitMqOperations operations,
        RabbitMqProvisioner provisioner,
        string queueName,
        IncomingBrokerMessage message,
        BrokerDisposition disposition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(provisioner);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(disposition);

        if (disposition is BrokerDisposition.Redeliver redeliver)
        {
            var delayQueue = await provisioner
                .EnsureDelayQueueAsync(operations, queueName, RabbitMqNames.DelaySeconds(redeliver.Delay), cancellationToken)
                .ConfigureAwait(false);
            var headers = new Dictionary<string, string>(message.Headers, StringComparer.Ordinal)
            {
                [BrokerHeaders.RedeliveryCount] = redeliver.RedeliveryCount.ToString(CultureInfo.InvariantCulture),
            };
            await operations.PublishAsync(string.Empty, delayQueue, mandatory: true, message.Body, headers, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (disposition is BrokerDisposition.DeadLetter deadLetter)
        {
            var errorQueue = await provisioner.EnsureErrorQueueAsync(operations, queueName, cancellationToken).ConfigureAwait(false);
            var headers = new Dictionary<string, string>(message.Headers, StringComparer.Ordinal)
            {
                [RabbitMqNames.DeadLetterReasonHeader] = deadLetter.Reason,
            };
            if (deadLetter.Detail is { } detail)
            {
                headers[RabbitMqNames.DeadLetterDetailHeader] = detail;
            }

            await operations.PublishAsync(string.Empty, errorQueue, mandatory: true, message.Body, headers, cancellationToken)
                .ConfigureAwait(false);
        }

        // Completed: the acknowledgement is all there is.
    }
}

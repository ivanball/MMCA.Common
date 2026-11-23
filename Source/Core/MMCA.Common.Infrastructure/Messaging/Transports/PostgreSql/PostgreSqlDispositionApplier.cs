using System.Globalization;
using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Infrastructure.Messaging.Transports.PostgreSql;

/// <summary>
/// Carries out a <see cref="BrokerDisposition"/> on a claimed row, each in one statement:
/// <list type="bullet">
///   <item><see cref="BrokerDisposition.Completed"/>: the row is deleted.</item>
///   <item><see cref="BrokerDisposition.Redeliver"/>: the row stays on the queue, hidden until the delay
///   has passed (<c>visible_after</c>), with <see cref="BrokerHeaders.RedeliveryCount"/> set, and the claim
///   is released.</item>
///   <item><see cref="BrokerDisposition.DeadLetter"/>: the row moves to <c>dead_letters</c> with the
///   reason and detail.</item>
/// </list>
/// </summary>
internal static class PostgreSqlDispositionApplier
{
    /// <summary>Applies <paramref name="disposition"/> to <paramref name="message"/>.</summary>
    /// <param name="store">The message store.</param>
    /// <param name="message">The claimed row.</param>
    /// <param name="disposition">The pipeline's decision.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    /// <returns><see langword="false"/> when the lease had already been lost, so another claim owns the row.</returns>
    internal static Task<bool> ApplyAsync(
        IPostgreSqlMessageStore store,
        PostgreSqlClaimedMessage message,
        BrokerDisposition disposition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(disposition);

        return disposition switch
        {
            BrokerDisposition.Redeliver redeliver => store.RedeliverAsync(
                message,
                redeliver.Delay,
                new Dictionary<string, string>(message.Headers, StringComparer.Ordinal)
                {
                    [BrokerHeaders.RedeliveryCount] = redeliver.RedeliveryCount.ToString(CultureInfo.InvariantCulture),
                },
                cancellationToken),
            BrokerDisposition.DeadLetter deadLetter => store.DeadLetterAsync(message, deadLetter.Reason, deadLetter.Detail, cancellationToken),
            _ => store.CompleteAsync(message, cancellationToken),
        };
    }
}

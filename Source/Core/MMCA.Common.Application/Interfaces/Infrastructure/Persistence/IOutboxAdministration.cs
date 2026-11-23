using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Interfaces.Infrastructure.Persistence;

/// <summary>
/// Operator surface over the outbox tables this host owns: inspect the dead letters, replay them,
/// and read the pending backlog. It exists so an undelivered event has a way BACK into delivery.
/// Without it the only terminal states are "eventually deleted by the retention sweep" and "edited
/// by hand in production SQL".
/// <para>
/// Expose it from an admin endpoint, a support command, or a scheduled job. Every method returns
/// <see cref="Result{T}"/>: an unreachable source or an unknown source name is an expected failure
/// an operator screen renders, not an exception.
/// </para>
/// </summary>
public interface IOutboxAdministration
{
    /// <summary>
    /// Lists dead-lettered rows (unprocessed, retries exhausted) oldest first, across every outbox
    /// source this host owns or just the one named.
    /// </summary>
    /// <param name="dataSource">
    /// Source name to restrict to, as reported by <see cref="OutboxDeadLetter.DataSource"/>;
    /// <see langword="null"/> lists every source.
    /// </param>
    /// <param name="skip">Rows to skip, for paging.</param>
    /// <param name="take">Maximum rows to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page of dead letters, oldest first.</returns>
    Task<Result<IReadOnlyList<OutboxDeadLetter>>> ListDeadLettersAsync(
        string? dataSource,
        int skip,
        int take,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns dead-lettered rows to the pending pool: <c>RetryCount</c> back to zero and the claim
    /// lease cleared, so the next poll cycle picks them up. <c>LastError</c> is deliberately KEPT,
    /// because the reason a message failed is the first thing anyone asks after a replay, and
    /// <c>OccurredOn</c> is untouched, so a replayed row keeps its place in its ordering key.
    /// </summary>
    /// <param name="dataSource">
    /// Source name to restrict to; <see langword="null"/> replays across every source this host owns.
    /// </param>
    /// <param name="ids">
    /// The specific rows to replay, or <see langword="null"/>/empty to replay EVERY dead letter in
    /// the selected scope.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows returned to the pending pool.</returns>
    Task<Result<int>> ReplayDeadLettersAsync(
        string? dataSource,
        IReadOnlyCollection<Guid>? ids,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the INTEGRATION-event rows processed inside <c>[fromUtc, toUtc)</c> to the pending
    /// pool, across every outbox source this host owns, so the processor publishes them again: the
    /// processed stamp, the retry count and the claim lease are cleared in one set-based update per
    /// source. <c>LastError</c> and <c>OccurredOn</c> are untouched, so a republished row keeps its
    /// place in its ordering key.
    /// <para>
    /// For a broker-side loss after a successful publish (and for a transport cutover, where the
    /// old revision may have published to entities nothing consumes any more). Delivery is
    /// at-least-once, so consumers absorb the duplicates through the inbox (ADR-021). Domain-event
    /// rows are never reset: their handlers run in-process with no inbox, and they already ran.
    /// Without the consumer inbox (the in-process message bus default, or a broker host that set
    /// <c>MessageBus:EnableInbox=false</c>) a republish would run those handlers again, so the call
    /// is refused with <c>Outbox.RepublishRequiresInbox</c> and nothing is touched.
    /// </para>
    /// </summary>
    /// <param name="fromUtc">Start of the processed-on window, inclusive. Must be UTC.</param>
    /// <param name="toUtc">End of the processed-on window, exclusive. Must be UTC, after <paramref name="fromUtc"/>,
    /// and at most seven days after it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows returned to the pending pool, a validation failure for an invalid window,
    /// or a conflict failure when the consumer inbox is disabled.</returns>
    Task<Result<int>> RepublishProcessedAsync(
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Counts rows still awaiting dispatch (unprocessed, retries not exhausted) across every outbox
    /// source this host owns or just the one named. Unlike the <c>outbox.pending.depth</c> gauge,
    /// which reports what the processor last observed, this counts the tables at the moment of the
    /// call and includes rows currently under a claim lease.
    /// </summary>
    /// <param name="dataSource">Source name to restrict to; <see langword="null"/> counts every source.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The pending row count.</returns>
    Task<Result<long>> CountPendingAsync(string? dataSource, CancellationToken cancellationToken);
}

/// <summary>
/// One dead-lettered outbox row, flattened for an operator view. The event PAYLOAD is deliberately
/// not projected: it can carry personal data (ADR-005), and nothing an operator decides about a
/// replay depends on reading it.
/// </summary>
/// <param name="Id">The outbox row id, the handle <see cref="IOutboxAdministration.ReplayDeadLettersAsync"/> takes.</param>
/// <param name="DataSource">The source whose outbox table holds the row.</param>
/// <param name="EventType">The stored event type name.</param>
/// <param name="OccurredOn">When the event was raised.</param>
/// <param name="RetryCount">Attempts made before the row was abandoned.</param>
/// <param name="LastError">The failure recorded on the final attempt.</param>
/// <param name="OrderingKey">The ordering key, when the event declared one.</param>
public sealed record OutboxDeadLetter(
    Guid Id,
    string DataSource,
    string EventType,
    DateTime OccurredOn,
    int RetryCount,
    string? LastError,
    string? OrderingKey);

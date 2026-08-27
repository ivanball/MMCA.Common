namespace MMCA.Common.Infrastructure.Persistence.Inbox;

/// <summary>
/// Consumer-side idempotency store. Lets the integration-event consumers skip an integration event
/// that has already been processed (at-least-once broker delivery can redeliver the same message).
/// The default implementation is a no-op; the EF-backed implementation is registered when the inbox
/// is enabled (<c>MessageBus:EnableInbox</c>, which defaults to ON for a broker transport).
/// <para>
/// The consume path is <see cref="TryBeginAsync"/> -> handlers -> <see cref="CompleteAsync"/>.
/// <see cref="TryBeginAsync"/> STAGES the inbox row in the same scoped unit of work the handlers
/// write through, so a handler's own <c>SaveChangesAsync</c> commits the row together with its
/// mutations and a crash between the two becomes impossible. <see cref="CompleteAsync"/> then saves
/// the row only if nothing else already did, which covers events whose handlers write nothing.
/// </para>
/// </summary>
public interface IInboxStore
{
    /// <summary>Returns whether an event with the given <paramref name="messageId"/> has already been processed.</summary>
    Task<bool> AlreadyProcessedAsync(Guid messageId, CancellationToken cancellationToken);

    /// <summary>Records that the event with the given <paramref name="messageId"/> has been processed.</summary>
    Task MarkProcessedAsync(Guid messageId, string eventType, CancellationToken cancellationToken);

    /// <summary>
    /// Opens processing for <paramref name="messageId"/>: returns <see langword="false"/> when the
    /// message was already processed (the caller must skip its handlers and ack), otherwise stages
    /// the inbox row in the consumer scope's unit of work and returns <see langword="true"/>.
    /// </summary>
    /// <param name="messageId">The broker message id being consumed.</param>
    /// <param name="eventType">The event type name, recorded for diagnostics.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the caller should run its handlers.</returns>
    /// <remarks>
    /// The default implementation only answers the question and stages nothing, so an external
    /// implementation of this interface keeps the pre-staging behavior (row written by
    /// <see cref="CompleteAsync"/> after the handlers) without a compile break.
    /// </remarks>
    async Task<bool> TryBeginAsync(Guid messageId, string eventType, CancellationToken cancellationToken)
        => !await AlreadyProcessedAsync(messageId, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Closes processing for <paramref name="messageId"/>: persists the row staged by
    /// <see cref="TryBeginAsync"/> if a handler's save has not already committed it. Idempotent.
    /// </summary>
    /// <param name="messageId">The broker message id being consumed.</param>
    /// <param name="eventType">The event type name, recorded for diagnostics.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CompleteAsync(Guid messageId, string eventType, CancellationToken cancellationToken)
        => MarkProcessedAsync(messageId, eventType, cancellationToken);

    /// <summary>
    /// Discards the row staged for <paramref name="messageId"/> after a handler failure, so the
    /// scope's context is not left holding a rejected insert and the broker's redelivery reprocesses
    /// the message.
    /// </summary>
    /// <param name="messageId">The broker message id whose consume attempt failed.</param>
    /// <returns>
    /// <see langword="true"/> when the staged row was discarded before it reached the database (the
    /// redelivery will reprocess); <see langword="false"/> when an earlier handler's save had
    /// already committed it, in which case the redelivery will be treated as a duplicate and the
    /// remaining handlers will NOT run again.
    /// </returns>
    bool Abandon(Guid messageId) => true;
}

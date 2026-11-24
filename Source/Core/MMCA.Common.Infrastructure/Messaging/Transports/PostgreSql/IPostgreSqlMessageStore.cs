using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Infrastructure.Messaging.Transports.PostgreSql;

/// <summary>
/// The database operations of the PostgreSQL transport, behind an interface so the receiver's loop and
/// the disposition handling are testable without a server. <see cref="PostgreSqlMessageStore"/> is the
/// one real implementation; every statement is in <see cref="PostgreSqlTransportSql"/>.
/// </summary>
internal interface IPostgreSqlMessageStore
{
    /// <summary>Creates the transport schema once per process (idempotent; a failed attempt is retried on the next call).</summary>
    /// <param name="cancellationToken">Stops waiting (the migration itself runs to completion).</param>
    /// <returns>A task that completes when the schema exists.</returns>
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    /// <summary>Binds <paramref name="topics"/> to <paramref name="queue"/> (idempotent).</summary>
    /// <param name="queue">The queue.</param>
    /// <param name="topics">The topics the queue consumes.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the bindings exist.</returns>
    Task SubscribeAsync(string queue, IReadOnlyCollection<string> topics, CancellationToken cancellationToken);

    /// <summary>Inserts one row per queue subscribed to the message's topic and signals those queues.</summary>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the rows are committed.</returns>
    Task PublishAsync(OutgoingBrokerMessage message, CancellationToken cancellationToken);

    /// <summary>Claims up to <paramref name="limit"/> due rows of <paramref name="queue"/> under a lease.</summary>
    /// <param name="queue">The queue.</param>
    /// <param name="limit">The most rows to claim.</param>
    /// <param name="lease">How long the claim holds before another receiver may take the row.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The claimed rows (possibly none).</returns>
    Task<IReadOnlyList<PostgreSqlClaimedMessage>> ClaimAsync(string queue, int limit, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>Deletes a handled row.</summary>
    /// <param name="message">The claimed row.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="false"/> when the lease was lost (another claim holds the row).</returns>
    Task<bool> CompleteAsync(PostgreSqlClaimedMessage message, CancellationToken cancellationToken);

    /// <summary>Hides a row for <paramref name="delay"/> with new headers and releases the claim.</summary>
    /// <param name="message">The claimed row.</param>
    /// <param name="delay">How long until the row is claimable again.</param>
    /// <param name="headers">The row's new headers.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="false"/> when the lease was lost.</returns>
    Task<bool> RedeliverAsync(PostgreSqlClaimedMessage message, TimeSpan delay, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken);

    /// <summary>Moves a row to <c>dead_letters</c> with a reason and detail.</summary>
    /// <param name="message">The claimed row.</param>
    /// <param name="reason">The dead-letter reason.</param>
    /// <param name="detail">The dead-letter detail, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="false"/> when the lease was lost.</returns>
    Task<bool> DeadLetterAsync(PostgreSqlClaimedMessage message, string reason, string? detail, CancellationToken cancellationToken);

    /// <summary>Releases a claim without settling the row, so it is claimable again at once.</summary>
    /// <param name="message">The claimed row.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="false"/> when the lease was already lost.</returns>
    Task<bool> ReleaseAsync(PostgreSqlClaimedMessage message, CancellationToken cancellationToken);

    /// <summary>Extends the lease of every row still held by one of <paramref name="messages"/>.</summary>
    /// <param name="messages">The in-flight rows.</param>
    /// <param name="lease">The new lease, from now.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the leases are extended.</returns>
    Task RenewAsync(IReadOnlyCollection<PostgreSqlClaimedMessage> messages, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>How long until the next row of <paramref name="queue"/> becomes claimable.</summary>
    /// <param name="queue">The queue.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The wait (zero or less when one is due now), or <see langword="null"/> when the queue is empty.</returns>
    Task<TimeSpan?> NextDueAsync(string queue, CancellationToken cancellationToken);

    /// <summary>
    /// Listens for publishes to <paramref name="queue"/> and calls <paramref name="notified"/> for each one,
    /// until <paramref name="cancellationToken"/> fires (then it returns) or the connection fails (then it throws).
    /// </summary>
    /// <param name="queue">The queue.</param>
    /// <param name="notified">Called on every notification for the queue, and once when listening starts.</param>
    /// <param name="cancellationToken">Stops listening.</param>
    /// <returns>A task that runs while listening.</returns>
    Task ListenAsync(string queue, Action notified, CancellationToken cancellationToken);
}

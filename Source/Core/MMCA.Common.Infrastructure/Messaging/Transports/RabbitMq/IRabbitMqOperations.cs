namespace MMCA.Common.Infrastructure.Messaging.Transports.RabbitMq;

/// <summary>
/// The broker operations the RabbitMQ transport performs outside the consume loop: declarations,
/// bindings and confirmed publishes. Kept behind an interface so naming, provisioning and disposition
/// handling are testable without a broker; <see cref="RabbitMqChannelOperations"/> is the one real
/// implementation.
/// </summary>
internal interface IRabbitMqOperations
{
    /// <summary>Declares a durable fanout exchange (idempotent).</summary>
    /// <param name="exchange">The exchange name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the broker has declared it.</returns>
    Task DeclareFanoutExchangeAsync(string exchange, CancellationToken cancellationToken);

    /// <summary>Declares a durable queue with <paramref name="arguments"/> (idempotent for identical arguments).</summary>
    /// <param name="queue">The queue name.</param>
    /// <param name="arguments">The declaration arguments, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the broker has declared it.</returns>
    Task DeclareQueueAsync(string queue, IDictionary<string, object?>? arguments, CancellationToken cancellationToken);

    /// <summary>Verifies that a queue exists without declaring it (a passive declaration); throws when it does not.</summary>
    /// <param name="queue">The queue name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the broker has confirmed the queue exists.</returns>
    Task EnsureQueueExistsAsync(string queue, CancellationToken cancellationToken);

    /// <summary>Binds <paramref name="queue"/> to the fanout <paramref name="exchange"/> (idempotent).</summary>
    /// <param name="queue">The queue name.</param>
    /// <param name="exchange">The exchange name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the broker has bound them.</returns>
    Task BindQueueAsync(string queue, string exchange, CancellationToken cancellationToken);

    /// <summary>
    /// Publishes a persistent message and waits for the broker's confirm. Throws when the broker nacks it,
    /// or, with <paramref name="mandatory"/>, when it could not be routed to any queue.
    /// </summary>
    /// <param name="exchange">The exchange (<c>""</c> for the default exchange).</param>
    /// <param name="routingKey">The routing key (the queue name on the default exchange).</param>
    /// <param name="mandatory">Whether an unroutable message is an error rather than silently dropped.</param>
    /// <param name="body">The body.</param>
    /// <param name="headers">The string headers, written as they are.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the broker has confirmed the message.</returns>
    Task PublishAsync(
        string exchange,
        string routingKey,
        bool mandatory,
        ReadOnlyMemory<byte> body,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken);
}

using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Infrastructure.Messaging.Transports.PostgreSql;

/// <summary>
/// The PostgreSQL <see cref="IBrokerPublisher"/>: one statement inserts a row for every queue subscribed
/// to the topic and notifies those queues. A topic is only a name in <c>subscriptions</c>, so there is
/// nothing to create on first publish; a topic no queue subscribes to inserts nothing. When this host
/// migrates the schema at startup, a publish that races the migration waits for it.
/// </summary>
/// <param name="store">The message store.</param>
/// <param name="migrateOnStartup">Whether to wait for the schema migration before publishing.</param>
internal sealed class PostgreSqlPublisher(IPostgreSqlMessageStore store, bool migrateOnStartup) : IBrokerPublisher
{
    /// <inheritdoc />
    public async Task PublishAsync(OutgoingBrokerMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (migrateOnStartup)
        {
            await store.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        }

        await store.PublishAsync(message, cancellationToken).ConfigureAwait(false);
    }
}

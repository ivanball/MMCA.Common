using System.Collections.Concurrent;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Infrastructure.Messaging.Transports.PostgreSql;

namespace MMCA.Common.Infrastructure.Tests.Messaging.Transports.PostgreSql;

/// <summary>
/// An in-memory <see cref="IPostgreSqlMessageStore"/>: rows waiting to be claimed, and a record of every
/// settle. Claims hand each pending row out once.
/// </summary>
internal sealed class FakePostgreSqlMessageStore : IPostgreSqlMessageStore
{
    private readonly ConcurrentQueue<PostgreSqlClaimedMessage> _pending = new();
    private long _nextId;

    private int _schemaCalls;

    public int SchemaCalls => Volatile.Read(ref _schemaCalls);

    public ConcurrentQueue<(string Queue, string[] Topics)> Subscriptions { get; } = new();

    public ConcurrentQueue<OutgoingBrokerMessage> Published { get; } = new();

    public ConcurrentQueue<PostgreSqlClaimedMessage> Completed { get; } = new();

    public ConcurrentQueue<(PostgreSqlClaimedMessage Message, TimeSpan Delay, IReadOnlyDictionary<string, string> Headers)> Redelivered { get; } = new();

    public ConcurrentQueue<(PostgreSqlClaimedMessage Message, string Reason, string? Detail)> DeadLettered { get; } = new();

    public ConcurrentQueue<PostgreSqlClaimedMessage> Released { get; } = new();

    public bool SettleResult { get; set; } = true;

    public PostgreSqlClaimedMessage Enqueue(string eventName = "order-placed")
    {
        var message = new PostgreSqlClaimedMessage(
            Interlocked.Increment(ref _nextId),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new Dictionary<string, string>(StringComparer.Ordinal) { [BrokerHeaders.EventName] = eventName },
            [1, 2],
            1);
        _pending.Enqueue(message);
        return message;
    }

    public Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _schemaCalls);
        return Task.CompletedTask;
    }

    public Task SubscribeAsync(string queue, IReadOnlyCollection<string> topics, CancellationToken cancellationToken)
    {
        Subscriptions.Enqueue((queue, topics.ToArray()));
        return Task.CompletedTask;
    }

    public Task PublishAsync(OutgoingBrokerMessage message, CancellationToken cancellationToken)
    {
        Published.Enqueue(message);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PostgreSqlClaimedMessage>> ClaimAsync(string queue, int limit, TimeSpan lease, CancellationToken cancellationToken)
    {
        List<PostgreSqlClaimedMessage> claimed = [];
        while (claimed.Count < limit && _pending.TryDequeue(out var message))
        {
            claimed.Add(message);
        }

        return Task.FromResult<IReadOnlyList<PostgreSqlClaimedMessage>>(claimed);
    }

    public Task<bool> CompleteAsync(PostgreSqlClaimedMessage message, CancellationToken cancellationToken)
    {
        Completed.Enqueue(message);
        return Task.FromResult(SettleResult);
    }

    public Task<bool> RedeliverAsync(PostgreSqlClaimedMessage message, TimeSpan delay, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        Redelivered.Enqueue((message, delay, headers));
        return Task.FromResult(SettleResult);
    }

    public Task<bool> DeadLetterAsync(PostgreSqlClaimedMessage message, string reason, string? detail, CancellationToken cancellationToken)
    {
        DeadLettered.Enqueue((message, reason, detail));
        return Task.FromResult(SettleResult);
    }

    public Task<bool> ReleaseAsync(PostgreSqlClaimedMessage message, CancellationToken cancellationToken)
    {
        Released.Enqueue(message);
        return Task.FromResult(true);
    }

    public Task RenewAsync(IReadOnlyCollection<PostgreSqlClaimedMessage> messages, TimeSpan lease, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<TimeSpan?> NextDueAsync(string queue, CancellationToken cancellationToken) =>
        Task.FromResult<TimeSpan?>(_pending.IsEmpty ? null : TimeSpan.Zero);

    public async Task ListenAsync(string queue, Action notified, CancellationToken cancellationToken)
    {
        notified();
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }
}

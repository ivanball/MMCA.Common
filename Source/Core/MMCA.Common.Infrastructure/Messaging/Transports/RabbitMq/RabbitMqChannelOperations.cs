using RabbitMQ.Client;

namespace MMCA.Common.Infrastructure.Messaging.Transports.RabbitMq;

/// <summary>
/// The real <see cref="IRabbitMqOperations"/>: one channel with publisher confirms (and confirm
/// tracking, so a publish awaits the broker's ack and throws on a nack or a mandatory return) on a
/// <see cref="RabbitMqConnectionSource"/>. Calls are serialized on the channel. A channel the broker
/// closed (a failed passive declaration or a declaration with conflicting arguments closes it) is
/// replaced on the next call.
/// </summary>
internal sealed class RabbitMqChannelOperations : IRabbitMqOperations, IAsyncDisposable, IDisposable
{
    private readonly RabbitMqConnectionSource _connections;
    private readonly bool _ownsConnections;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChannel? _channel;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="RabbitMqChannelOperations"/> class.</summary>
    /// <param name="connections">The connection the channel is opened on.</param>
    /// <param name="ownsConnections">Whether disposing this instance also disposes <paramref name="connections"/>.</param>
    public RabbitMqChannelOperations(RabbitMqConnectionSource connections, bool ownsConnections)
    {
        ArgumentNullException.ThrowIfNull(connections);

        _connections = connections;
        _ownsConnections = ownsConnections;
    }

    /// <inheritdoc />
    public Task DeclareFanoutExchangeAsync(string exchange, CancellationToken cancellationToken) =>
        RunAsync(
            channel => channel.ExchangeDeclareAsync(exchange, ExchangeType.Fanout, durable: true, autoDelete: false, arguments: null, cancellationToken: cancellationToken),
            cancellationToken);

    /// <inheritdoc />
    public Task DeclareQueueAsync(string queue, IDictionary<string, object?>? arguments, CancellationToken cancellationToken) =>
        RunAsync(
            channel => channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, arguments: arguments, cancellationToken: cancellationToken),
            cancellationToken);

    /// <inheritdoc />
    public Task EnsureQueueExistsAsync(string queue, CancellationToken cancellationToken) =>
        RunAsync(channel => channel.QueueDeclarePassiveAsync(queue, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task BindQueueAsync(string queue, string exchange, CancellationToken cancellationToken) =>
        RunAsync(
            channel => channel.QueueBindAsync(queue, exchange, routingKey: string.Empty, arguments: null, cancellationToken: cancellationToken),
            cancellationToken);

    /// <inheritdoc />
    public Task PublishAsync(
        string exchange,
        string routingKey,
        bool mandatory,
        ReadOnlyMemory<byte> body,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var properties = new BasicProperties
        {
            Persistent = true,
            Headers = RabbitMqHeaders.Encode(headers),
            ContentType = headers.TryGetValue(Broker.BrokerHeaders.ContentType, out var contentType) ? contentType : null,
            MessageId = headers.TryGetValue(Broker.BrokerHeaders.MessageId, out var messageId) ? messageId : null,
        };

        return RunAsync(
            async channel => await channel.BasicPublishAsync(exchange, routingKey, mandatory, properties, body, cancellationToken).ConfigureAwait(false),
            cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_channel is { } channel)
        {
            await CloseQuietlyAsync(channel).ConfigureAwait(false);
        }

        if (_ownsConnections)
        {
            await _connections.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }

    /// <summary>Disposes synchronously, for a container disposed without <see langword="await"/>.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _channel?.Dispose();
        if (_ownsConnections)
        {
            _connections.Dispose();
        }

        _gate.Dispose();
    }

    private static async Task CloseQuietlyAsync(IChannel channel)
    {
        try
        {
            if (channel.IsOpen)
            {
                await channel.CloseAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The channel or its connection is already gone; there is nothing left to close.
        }

        await channel.DisposeAsync().ConfigureAwait(false);
    }

    private async Task RunAsync(Func<IChannel, Task> operation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = await GetChannelAsync(cancellationToken).ConfigureAwait(false);
            await operation(channel).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true } open)
        {
            return open;
        }

        if (_channel is { } closed)
        {
            // Closed by the broker (a channel-level error), not by a connection loss that automatic
            // recovery would repair: replace it.
            _channel = null;
            await CloseQuietlyAsync(closed).ConfigureAwait(false);
        }

        var connection = await _connections.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        _channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken).ConfigureAwait(false);
        return _channel;
    }
}

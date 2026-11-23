using RabbitMQ.Client;

namespace MMCA.Common.Infrastructure.Messaging.Transports.RabbitMq;

/// <summary>
/// One lazily opened RabbitMQ connection with automatic recovery. Opening is deferred to the first use
/// so a host whose broker is down still starts (the outbox holds what it cannot publish); a failed open
/// is not cached, so the next call tries again. The publisher and the receiver each own one, so a
/// publisher under broker flow control never stalls the consumer's acknowledgements.
/// </summary>
internal sealed class RabbitMqConnectionSource : IAsyncDisposable, IDisposable
{
    private readonly ConnectionFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="RabbitMqConnectionSource"/> class.</summary>
    /// <param name="connectionUri">The AMQP URI, or <see langword="null"/> for the client's default (<c>amqp://guest:guest@localhost:5672</c>).</param>
    /// <param name="clientProvidedName">The connection name shown in the RabbitMQ management UI.</param>
    public RabbitMqConnectionSource(Uri? connectionUri, string clientProvidedName) =>
        _factory = CreateFactory(connectionUri, clientProvidedName);

    /// <summary>
    /// Builds the connection factory: automatic connection and topology recovery on, so a broker restart
    /// re-opens the connection, its channels and the consumer without the transport's help.
    /// </summary>
    /// <param name="connectionUri">The AMQP URI, or <see langword="null"/>.</param>
    /// <param name="clientProvidedName">The connection name.</param>
    /// <returns>The factory.</returns>
    internal static ConnectionFactory CreateFactory(Uri? connectionUri, string clientProvidedName)
    {
        var factory = new ConnectionFactory
        {
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
            ClientProvidedName = clientProvidedName,
        };

        if (connectionUri is not null)
        {
            factory.Uri = connectionUri;
        }

        return factory;
    }

    /// <summary>Returns the open connection, opening it on first use.</summary>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The connection.</returns>
    public async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_connection is { } existing)
        {
            return existing;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _connection ??= await _factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_connection is { } connection)
        {
            try
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Closing a connection the broker already dropped has nothing left to release.
            }

            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }

    /// <summary>
    /// Disposes synchronously, for a container disposed without <see langword="await"/>: the connection is
    /// dropped rather than closed with a handshake.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection?.Dispose();
        _gate.Dispose();
    }
}

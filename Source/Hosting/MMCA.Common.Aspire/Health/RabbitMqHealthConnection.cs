using RabbitMQ.Client;

namespace MMCA.Common.Aspire.Health;

/// <summary>
/// The one AMQP connection the RabbitMQ health check probes through.
/// <para>
/// The <c>AspNetCore.HealthChecks.Rabbitmq</c> check calls its connection factory on every probe round
/// and never disposes what it returns, so a factory that opened a connection per call leaked one AMQP
/// connection on the broker every successful round. This singleton holds at most one: it hands out the
/// held connection while it is open, and replaces it (disposing the old one, best effort) only when it
/// is gone or no longer open. Creation is single flight, bounded by the check timeout (a token cancelled
/// at the timeout, and the wait abandoned at the timeout even if the client ignores that token), and a
/// failed or abandoned creation is not kept, so the next round tries again. The container disposes it
/// when the host stops, which closes the held connection.
/// </para>
/// <para>
/// After <see cref="DisposeAsync"/>, <see cref="GetAsync"/> throws <see cref="ObjectDisposedException"/>,
/// which the health check reports as its failure status.
/// </para>
/// </summary>
internal sealed class RabbitMqHealthConnection : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<IConnection>> _connect;

    private readonly TimeSpan _timeout;

    private readonly Lock _sync = new();

    private IConnection? _connection;

    private Task<IConnection>? _creating;

    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="RabbitMqHealthConnection"/> class for a broker URI.</summary>
    /// <param name="uri">The broker's AMQP URI.</param>
    /// <param name="timeout">How long one connection attempt may take.</param>
    public RabbitMqHealthConnection(Uri uri, TimeSpan timeout)
        : this(
            cancellationToken => new ConnectionFactory { Uri = uri }.CreateConnectionAsync(cancellationToken),
            timeout) => ArgumentNullException.ThrowIfNull(uri);

    /// <summary>Initializes a new instance of the <see cref="RabbitMqHealthConnection"/> class over a creation delegate.</summary>
    /// <param name="connect">Creates one connection; the token is cancelled at <paramref name="timeout"/>.</param>
    /// <param name="timeout">How long one connection attempt may take.</param>
    internal RabbitMqHealthConnection(Func<CancellationToken, Task<IConnection>> connect, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        _connect = connect;
        _timeout = timeout;
    }

    /// <summary>
    /// The held connection while it is open; otherwise a new one, created once however many callers
    /// ask at the same time.
    /// </summary>
    /// <param name="cancellationToken">Stops THIS caller waiting; the shared creation keeps its own timeout.</param>
    /// <returns>An open connection.</returns>
    /// <exception cref="ObjectDisposedException">The holder was disposed.</exception>
    /// <exception cref="TimeoutException">The creation did not finish within the timeout.</exception>
    public Task<IConnection> GetAsync(CancellationToken cancellationToken)
    {
        Task<IConnection> creating;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_connection is { IsOpen: true } open)
            {
                return Task.FromResult(open);
            }

            if (_creating is null)
            {
                // The stale connection leaves the holder now, so no later caller can be handed it;
                // it is disposed by the creation, outside this lock.
                var stale = _connection;
                _connection = null;
                _creating = Task.Run(() => CreateAsync(stale), CancellationToken.None);
            }

            creating = _creating;
        }

        return creating.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        IConnection? held;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            held = _connection;
            _connection = null;
        }

        // A creation still in flight sees the flag when it finishes and disposes its own result.
        if (held is not null)
        {
            await DisposeQuietlyAsync(held).ConfigureAwait(false);
        }
    }

    /// <summary>Disposes a connection, best effort: a connection that fails to close is already of no use.</summary>
    private static async Task DisposeQuietlyAsync(IConnection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Best effort: the replacement, or the host stopping, matters more than this one's close.
        }
    }

    /// <summary>Cancels a token source, best effort: a callback that throws must not replace the timeout.</summary>
    private static async Task CancelQuietlyAsync(CancellationTokenSource source)
    {
        try
        {
            await source.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Best effort: the caller is already reporting the timeout.
        }
    }

    /// <summary>
    /// Disposes the result of a creation that was abandoned at the timeout, should it ever finish, and
    /// only then the token source that creation was given.
    /// </summary>
    private static async Task DisposeWhenCreatedAsync(Task<IConnection> creation, CancellationTokenSource bounded)
    {
        try
        {
            var late = await creation.ConfigureAwait(false);
            await DisposeQuietlyAsync(late).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The abandoned attempt failed after all: nothing to release.
        }
        finally
        {
            bounded.Dispose();
        }
    }

    /// <summary>
    /// One connection attempt: its token is cancelled at the timeout, and the wait ends at the timeout
    /// even if the client ignores that token. An attempt abandoned that way takes its token source with
    /// it, disposed (with any connection it still produces) once the attempt ends.
    /// </summary>
    private async Task<IConnection> ConnectWithinTimeoutAsync()
    {
        CancellationTokenSource? bounded = new(_timeout);
        try
        {
            var creation = _connect(bounded.Token);
            try
            {
                return await creation.WaitAsync(_timeout, TimeProvider.System).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                var owned = bounded;
                bounded = null;

                // The wait can end a moment before the token's own timer fires. Cancel it here, so an
                // abandoned creation that honours its token is always told to stop.
                await CancelQuietlyAsync(owned).ConfigureAwait(false);
                _ = DisposeWhenCreatedAsync(creation, owned);
                throw;
            }
        }
        finally
        {
            bounded?.Dispose();
        }
    }

    private async Task<IConnection> CreateAsync(IConnection? stale)
    {
        try
        {
            if (stale is not null)
            {
                await DisposeQuietlyAsync(stale).ConfigureAwait(false);
            }

            var connection = await ConnectWithinTimeoutAsync().ConfigureAwait(false);

            lock (_sync)
            {
                if (!_disposed)
                {
                    _connection = connection;
                    return connection;
                }
            }

            await DisposeQuietlyAsync(connection).ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(RabbitMqHealthConnection));
        }
        finally
        {
            lock (_sync)
            {
                _creating = null;
            }
        }
    }
}

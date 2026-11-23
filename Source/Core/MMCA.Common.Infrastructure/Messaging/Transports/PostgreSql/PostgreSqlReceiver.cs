using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Infrastructure.Messaging.Transports.PostgreSql;

/// <summary>
/// The PostgreSQL receiver: claims due rows of the host queue (<c>FOR UPDATE SKIP LOCKED</c> under a
/// lease, so competing receivers never take the same row), hands each to
/// <see cref="IBrokerReceivePipeline"/> and applies the disposition. Claimed rows' leases are renewed
/// while they are in flight. It sleeps until a <c>LISTEN</c> notification for the queue arrives, the
/// next row becomes due, or <see cref="PostgreSqlReceiverOptions.PollInterval"/> passes, whichever comes
/// first. A row whose processing is cut short by the host stopping has its claim released, so another
/// receiver takes it at once.
/// <para>
/// The receiver never stops the host: a database outage is logged as a Warning and retried every
/// <see cref="RetryDelay"/>.
/// </para>
/// </summary>
internal sealed partial class PostgreSqlReceiver : BackgroundService
{
    /// <summary>The wait after a failed claim, listen or startup step.</summary>
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>The shortest sleep between two empty claims, so contended rows do not spin the loop.</summary>
    private static readonly TimeSpan MinimumWait = TimeSpan.FromMilliseconds(100);

    /// <summary>The longest a stopping receiver waits for in-flight rows to settle.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    private readonly BrokerTopology _topology;
    private readonly IBrokerReceivePipeline _pipeline;
    private readonly IPostgreSqlMessageStore _store;
    private readonly PostgreSqlReceiverOptions _options;
    private readonly ILogger<PostgreSqlReceiver> _logger;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<long, PostgreSqlClaimedMessage> _inFlight = new();
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    /// <summary>Initializes a new instance of the <see cref="PostgreSqlReceiver"/> class.</summary>
    /// <param name="topology">The host topology.</param>
    /// <param name="pipeline">The framework receive pipeline.</param>
    /// <param name="store">The message store.</param>
    /// <param name="options">The receiver options.</param>
    /// <param name="logger">The logger.</param>
    public PostgreSqlReceiver(
        BrokerTopology topology,
        IBrokerReceivePipeline pipeline,
        IPostgreSqlMessageStore store,
        PostgreSqlReceiverOptions options,
        ILogger<PostgreSqlReceiver> logger)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _topology = topology;
        _pipeline = pipeline;
        _store = store;
        _options = options;
        _logger = logger;
        _slots = new SemaphoreSlim(options.Concurrency, options.Concurrency);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _slots.Dispose();
        base.Dispose();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await PrepareAsync(stoppingToken).ConfigureAwait(false))
        {
            return;
        }

        var listener = ListenLoopAsync(stoppingToken);
        var renewer = RenewLoopAsync(stoppingToken);
        LogReceiving(_logger, _topology.QueueName, _options.Concurrency);

        try
        {
            await ReceiveLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
        finally
        {
            await DrainAsync().ConfigureAwait(false);
            await Task.WhenAll(listener, renewer).ConfigureAwait(false);
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<bool> PrepareAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            try
            {
                if (_options.MigrateOnStartup)
                {
                    await _store.EnsureSchemaAsync(stoppingToken).ConfigureAwait(false);
                }

                if (_options.ProvisionTopology)
                {
                    await _store.SubscribeAsync(
                        _topology.QueueName,
                        [.. _topology.Subscriptions.Select(s => s.Topic).Distinct(StringComparer.Ordinal)],
                        stoppingToken).ConfigureAwait(false);
                }

                return true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogStepFailed(_logger, ex, "start", _topology.QueueName, RetryDelay);
            }

            if (!await DelayAsync(RetryDelay, stoppingToken).ConfigureAwait(false))
            {
                return false;
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await _slots.WaitAsync(stoppingToken).ConfigureAwait(false);
            var taken = 1;
            while (taken < _options.ClaimBatchSize && await _slots.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
            {
                taken++;
            }

            IReadOnlyList<PostgreSqlClaimedMessage> claimed;
            try
            {
                claimed = await _store.ClaimAsync(_topology.QueueName, taken, _options.Lease, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _slots.Release(taken);
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                LogStepFailed(_logger, ex, "claim", _topology.QueueName, RetryDelay);
                await DelayAsync(RetryDelay, stoppingToken).ConfigureAwait(false);
                continue;
            }

            if (claimed.Count < taken)
            {
                _slots.Release(taken - claimed.Count);
            }

            foreach (var message in claimed)
            {
                _inFlight[message.Id] = message;
                _ = Task.Run(() => RunAsync(message, stoppingToken), CancellationToken.None);
            }

            if (claimed.Count == 0)
            {
                await WaitForWorkAsync(stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunAsync(PostgreSqlClaimedMessage message, CancellationToken stoppingToken)
    {
        try
        {
            await ProcessAsync(message, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(message.Id, out _);
            try
            {
                _slots.Release();
            }
            catch (ObjectDisposedException)
            {
                // Settled after the stopping receiver stopped waiting for it and was disposed.
            }
        }
    }

    private async Task ProcessAsync(PostgreSqlClaimedMessage message, CancellationToken stoppingToken)
    {
        var incoming = new IncomingBrokerMessage(message.Body, message.Headers, message.Id.ToString(CultureInfo.InvariantCulture));
        try
        {
            var disposition = await _pipeline.ProcessAsync(incoming, stoppingToken).ConfigureAwait(false);

            // Settled without the host's token: once the pipeline has decided, the decision is written
            // even when the host starts stopping meanwhile.
            if (!await PostgreSqlDispositionApplier.ApplyAsync(_store, message, disposition, CancellationToken.None).ConfigureAwait(false))
            {
                LogLeaseLost(_logger, message.Id, _topology.QueueName);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await ReleaseQuietlyAsync(message).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogSettleFailed(_logger, ex, message.Id, _topology.QueueName);
            await ReleaseQuietlyAsync(message).ConfigureAwait(false);
        }
    }

    private async Task ReleaseQuietlyAsync(PostgreSqlClaimedMessage message)
    {
        try
        {
            await _store.ReleaseAsync(message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The lease expires on its own, and the row is claimable again then.
            LogReleaseFailed(_logger, ex, message.Id, _topology.QueueName);
        }
    }

    private async Task WaitForWorkAsync(CancellationToken stoppingToken)
    {
        var wait = _options.PollInterval;
        try
        {
            if (await _store.NextDueAsync(_topology.QueueName, stoppingToken).ConfigureAwait(false) is { } due && due < wait)
            {
                wait = due;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !stoppingToken.IsCancellationRequested)
        {
            LogStepFailed(_logger, ex, "next-due", _topology.QueueName, wait);
        }

        if (wait < MinimumWait)
        {
            wait = MinimumWait;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(wait);
        try
        {
            await _wake.Reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false);
            _wake.Reader.TryRead(out _);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // The wait elapsed with no notification: look again.
        }
    }

    private async Task ListenLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _store.ListenAsync(_topology.QueueName, () => _wake.Writer.TryWrite(true), stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                // Polling keeps the queue moving while the listener is down.
                LogStepFailed(_logger, ex, "listen", _topology.QueueName, RetryDelay);
            }

            if (!await DelayAsync(RetryDelay, stoppingToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private async Task RenewLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.Lease / 3);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var inFlight = _inFlight.Values.ToArray();
                if (inFlight.Length == 0)
                {
                    continue;
                }

                try
                {
                    await _store.RenewAsync(inFlight, _options.Lease, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && !stoppingToken.IsCancellationRequested)
                {
                    LogStepFailed(_logger, ex, "renew", _topology.QueueName, _options.Lease / 3);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    private async Task DrainAsync()
    {
        var waited = TimeSpan.Zero;
        var step = TimeSpan.FromMilliseconds(50);
        while (_slots.CurrentCount < _options.Concurrency && waited < DrainTimeout)
        {
            await Task.Delay(step, CancellationToken.None).ConfigureAwait(false);
            waited += step;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "PostgreSQL receiver consuming queue {Queue} (concurrency {Concurrency})")]
    private static partial void LogReceiving(ILogger logger, string queue, int concurrency);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PostgreSQL receiver step '{Step}' for queue {Queue} failed; retrying in {Delay}")]
    private static partial void LogStepFailed(ILogger logger, Exception ex, string step, string queue, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PostgreSQL message {RowId} on queue {Queue} was settled after its lease had passed to another claim; the other claim settles it")]
    private static partial void LogLeaseLost(ILogger logger, long rowId, string queue);

    [LoggerMessage(Level = LogLevel.Error, Message = "PostgreSQL message {RowId} on queue {Queue} could not be settled; its claim is released")]
    private static partial void LogSettleFailed(ILogger logger, Exception ex, long rowId, string queue);

    [LoggerMessage(Level = LogLevel.Debug, Message = "PostgreSQL claim release for message {RowId} on queue {Queue} failed; the lease expires instead")]
    private static partial void LogReleaseFailed(ILogger logger, Exception ex, long rowId, string queue);
}

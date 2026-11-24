using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMCA.Common.Infrastructure.Messaging.Broker;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace MMCA.Common.Infrastructure.Messaging.Transports.RabbitMq;

/// <summary>
/// The RabbitMQ receiver: consumes the host queue with manual acknowledgements, hands every delivery to
/// <see cref="IBrokerReceivePipeline"/> and applies the disposition (publishing the redelivery or
/// dead-letter copy first, then acknowledging the original). A delivery whose processing is cut short by
/// the host stopping is negatively acknowledged with requeue, so the broker delivers it again.
/// <para>
/// The receiver never stops the host. When the broker is unreachable at startup (or a channel is lost
/// and not recovered) it logs a Warning and tries again every <see cref="ReconnectDelay"/>; the
/// client's automatic recovery repairs an ordinary connection loss on its own.
/// </para>
/// </summary>
internal sealed partial class RabbitMqReceiver : BackgroundService
{
    /// <summary>The wait between two attempts to (re)open the consume session.</summary>
    internal static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    /// <summary>How often the session checks for a channel the broker closed without recovery.</summary>
    private static readonly TimeSpan ChannelCheckInterval = TimeSpan.FromSeconds(10);

    /// <summary>The longest a stopping receiver waits for in-flight deliveries to settle.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    private readonly BrokerTopology _topology;
    private readonly IBrokerReceivePipeline _pipeline;
    private readonly RabbitMqConnectionSource _connections;
    private readonly RabbitMqChannelOperations _operations;
    private readonly RabbitMqProvisioner _provisioner;
    private readonly ushort _prefetchCount;
    private readonly ushort _concurrency;
    private readonly ILogger<RabbitMqReceiver> _logger;
    private readonly string _queue;
    private int _inFlight;

    /// <summary>Initializes a new instance of the <see cref="RabbitMqReceiver"/> class.</summary>
    /// <param name="topology">The host topology.</param>
    /// <param name="pipeline">The framework receive pipeline.</param>
    /// <param name="connections">The receiver's own connection; disposed when the receiver stops.</param>
    /// <param name="provisioner">Declares the receive topology and the delay and error queues.</param>
    /// <param name="prefetchCount">The channel's prefetch window.</param>
    /// <param name="concurrency">How many deliveries are processed at once.</param>
    /// <param name="logger">The logger.</param>
    public RabbitMqReceiver(
        BrokerTopology topology,
        IBrokerReceivePipeline pipeline,
        RabbitMqConnectionSource connections,
        RabbitMqProvisioner provisioner,
        ushort prefetchCount,
        ushort concurrency,
        ILogger<RabbitMqReceiver> logger)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(provisioner);
        ArgumentNullException.ThrowIfNull(logger);

        _topology = topology;
        _pipeline = pipeline;
        _connections = connections;
        _operations = new RabbitMqChannelOperations(connections, ownsConnections: false);
        _provisioner = provisioner;
        _prefetchCount = prefetchCount;
        _concurrency = concurrency;
        _logger = logger;
        _queue = RabbitMqNames.Queue(topology.QueueName);
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await _operations.DisposeAsync().ConfigureAwait(false);
            await _connections.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        // Already disposed asynchronously when the host stopped the receiver; both calls are no-ops then.
        _operations.Dispose();
        _connections.Dispose();
        base.Dispose();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogSessionFailed(_logger, ex, _queue, ReconnectDelay);
            }

            try
            {
                await Task.Delay(ReconnectDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
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
            // Already closed by the broker or by a connection loss.
        }

        await channel.DisposeAsync().ConfigureAwait(false);
    }

    private async Task RunSessionAsync(CancellationToken stoppingToken)
    {
        var connection = await _connections.GetConnectionAsync(stoppingToken).ConfigureAwait(false);
        await _provisioner.ProvisionReceiveAsync(_operations, _topology, stoppingToken).ConfigureAwait(false);
        if (!_provisioner.ProvisionTopology)
        {
            await _operations.EnsureQueueExistsAsync(_queue, stoppingToken).ConfigureAwait(false);
        }

        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: false,
                publisherConfirmationTrackingEnabled: false,
                consumerDispatchConcurrency: _concurrency),
            stoppingToken).ConfigureAwait(false);
        try
        {
            await channel.BasicQosAsync(0, _prefetchCount, global: false, stoppingToken).ConfigureAwait(false);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (_, delivery) => HandleAsync(channel, delivery, stoppingToken);
            await channel.BasicConsumeAsync(_queue, autoAck: false, consumer, stoppingToken).ConfigureAwait(false);
            LogConsuming(_logger, _queue, _prefetchCount, _concurrency);

            while (true)
            {
                await Task.Delay(ChannelCheckInterval, stoppingToken).ConfigureAwait(false);

                // A connection loss closes the channel too, and automatic recovery reopens both. A
                // channel the BROKER closed while the connection stayed up is never recovered: start a
                // new session.
                if (channel.IsClosed && connection.IsOpen)
                {
                    LogChannelLost(_logger, _queue);
                    return;
                }
            }
        }
        finally
        {
            await DrainAsync().ConfigureAwait(false);
            await CloseQuietlyAsync(channel).ConfigureAwait(false);
        }
    }

    private async Task DrainAsync()
    {
        var waited = TimeSpan.Zero;
        var step = TimeSpan.FromMilliseconds(50);
        while (Volatile.Read(ref _inFlight) > 0 && waited < DrainTimeout)
        {
            await Task.Delay(step, CancellationToken.None).ConfigureAwait(false);
            waited += step;
        }
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken stoppingToken)
    {
        Interlocked.Increment(ref _inFlight);
        try
        {
            var message = new IncomingBrokerMessage(
                delivery.Body.ToArray(),
                RabbitMqHeaders.Decode(delivery.BasicProperties.Headers),
                delivery.BasicProperties.MessageId ?? delivery.DeliveryTag.ToString(CultureInfo.InvariantCulture));

            try
            {
                var disposition = await _pipeline.ProcessAsync(message, stoppingToken).ConfigureAwait(false);
                await RabbitMqDispositionApplier.ApplyAsync(_operations, _provisioner, _topology.QueueName, message, disposition, stoppingToken)
                    .ConfigureAwait(false);
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // The host is stopping: leave the message to the broker.
                await RequeueQuietlyAsync(channel, delivery.DeliveryTag).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogDispositionFailed(_logger, ex, message.BrokerMessageId, _queue);
                await RequeueQuietlyAsync(channel, delivery.DeliveryTag).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private async Task RequeueQuietlyAsync(IChannel channel, ulong deliveryTag)
    {
        try
        {
            await channel.BasicNackAsync(deliveryTag, multiple: false, requeue: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A closed channel returns every unacknowledged delivery to the queue by itself.
            LogRequeueFailed(_logger, ex, _queue);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "RabbitMQ receiver consuming queue {Queue} (prefetch {PrefetchCount}, concurrency {Concurrency})")]
    private static partial void LogConsuming(ILogger logger, string queue, ushort prefetchCount, ushort concurrency);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RabbitMQ receiver for queue {Queue} could not consume; retrying in {Delay}")]
    private static partial void LogSessionFailed(ILogger logger, Exception ex, string queue, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RabbitMQ consume channel for queue {Queue} was closed by the broker; reopening")]
    private static partial void LogChannelLost(ILogger logger, string queue);

    [LoggerMessage(Level = LogLevel.Error, Message = "RabbitMQ message {BrokerMessageId} on queue {Queue} could not be settled; returned to the queue")]
    private static partial void LogDispositionFailed(ILogger logger, Exception ex, string brokerMessageId, string queue);

    [LoggerMessage(Level = LogLevel.Debug, Message = "RabbitMQ requeue on queue {Queue} failed; the closing channel returns the message instead")]
    private static partial void LogRequeueFailed(ILogger logger, Exception ex, string queue);
}

using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Infrastructure.Messaging.Transports.RabbitMq;

/// <summary>
/// The RabbitMQ <see cref="IBrokerPublisher"/>: publishes each message, persistent and confirmed, to its
/// topic's fanout exchange, declaring the exchange first when provisioning. A publish the broker did not
/// confirm throws, so the outbox keeps the row and retries it.
/// </summary>
internal sealed class RabbitMqPublisher : IBrokerPublisher, IAsyncDisposable, IDisposable
{
    private readonly IRabbitMqOperations _operations;
    private readonly RabbitMqProvisioner _provisioner;

    /// <summary>Initializes a new instance of the <see cref="RabbitMqPublisher"/> class.</summary>
    /// <param name="operations">The broker operations; disposed with the publisher when disposable.</param>
    /// <param name="provisioner">Declares a topic's exchange before its first publish.</param>
    public RabbitMqPublisher(IRabbitMqOperations operations, RabbitMqProvisioner provisioner)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(provisioner);

        _operations = operations;
        _provisioner = provisioner;
    }

    /// <inheritdoc />
    public async Task PublishAsync(OutgoingBrokerMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var exchange = RabbitMqNames.Exchange(message.Topic);
        try
        {
            await _provisioner.EnsureExchangeAsync(_operations, exchange, cancellationToken).ConfigureAwait(false);

            // Not mandatory: a fanout exchange with no queue bound yet means no host consumes the event,
            // which is not an error for the publisher.
            await _operations.PublishAsync(exchange, string.Empty, mandatory: false, message.Body, message.Headers, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            _provisioner.ForgetExchange(exchange);
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() =>
        _operations is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;

    /// <summary>Disposes synchronously, for a container disposed without <see langword="await"/>.</summary>
    public void Dispose() => (_operations as IDisposable)?.Dispose();
}

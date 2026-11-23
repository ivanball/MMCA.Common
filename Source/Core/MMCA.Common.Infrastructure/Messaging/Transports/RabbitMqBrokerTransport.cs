using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.Infrastructure.Messaging.Transports;

/// <summary>
/// The built-in RabbitMQ transport (<c>MessageBus:Provider=RabbitMq</c>), the broker the Aspire
/// development stack and the cross-service test tier run. Written against the same
/// <see cref="IBrokerTransport"/> contract an adapter package implements.
/// <para>
/// PLACEHOLDER on <c>feat/native-messaging</c>: the native RabbitMQ.Client implementation (a fanout
/// exchange per event, one durable queue per host, per-interval delay queues for redelivery and a
/// <c>{queue}-error</c> queue) replaces this class before the branch merges.
/// </para>
/// </summary>
internal sealed class RabbitMqBrokerTransport : IBrokerTransport
{
    /// <inheritdoc />
    public string Name => MessageBusProviders.RabbitMq;

    /// <inheritdoc />
    public TimeSpan? MaxRedeliveryDelay => null;

    /// <inheritdoc />
    public void Register(IServiceCollection services, BrokerTransportContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);

        throw new NotSupportedException("RabbitMQ transport: native implementation pending on feat/native-messaging");
    }
}

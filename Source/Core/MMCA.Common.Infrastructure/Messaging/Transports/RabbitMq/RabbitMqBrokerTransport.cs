using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Infrastructure.Messaging.Transports.RabbitMq;

/// <summary>
/// The built-in RabbitMQ transport (<c>MessageBus:Provider=RabbitMq</c>), the broker the Aspire
/// development stack and the cross-service test tier run, written on RabbitMQ.Client against the same
/// <see cref="IBrokerTransport"/> contract an adapter package implements.
/// <para>
/// Topology (<see cref="RabbitMqNames"/>): a durable fanout exchange per topic, one durable queue per
/// host bound to the exchange of every event it consumes, <c>{queue}-error</c> for dead letters, and
/// <c>{queue}-delay-{seconds}s</c> per redelivery interval, a queue whose message time to live returns
/// each message to the host queue through the default exchange, so delayed redelivery needs no plugin.
/// Publishes are persistent and confirmed; connections recover automatically.
/// </para>
/// <para>
/// The connection string is the shared broker one (<c>MessageBus:ConnectionString</c>, then
/// <c>ConnectionStrings:rabbitmq</c>, which Aspire injects, then <c>ConnectionStrings:messaging</c>), an
/// <c>amqp://</c> or <c>amqps://</c> URI; with none the client's default (<c>localhost:5672</c>) applies.
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

        var uri = ParseConnectionUri(context.ConnectionString);
        var topology = context.Topology;
        var provisionTopology = context.Settings.ProvisionTopology;

        services.AddSingleton<IBrokerPublisher>(_ => new RabbitMqPublisher(
            new RabbitMqChannelOperations(new RabbitMqConnectionSource(uri, topology.QueueName + " (publish)"), ownsConnections: true),
            new RabbitMqProvisioner(provisionTopology)));

        if (topology.Subscriptions.Count == 0)
        {
            return;
        }

        var concurrency = ResolveConcurrency(context.Settings);
        var prefetchCount = ResolvePrefetchCount(context.Settings, concurrency);
        services.AddHostedService(sp => new RabbitMqReceiver(
            topology,
            sp.GetRequiredService<IBrokerReceivePipeline>(),
            new RabbitMqConnectionSource(uri, topology.QueueName + " (receive)"),
            new RabbitMqProvisioner(provisionTopology),
            prefetchCount,
            concurrency,
            sp.GetRequiredService<ILogger<RabbitMqReceiver>>()));
    }

    /// <summary>
    /// Parses the broker connection string as an AMQP URI, or returns <see langword="null"/> when none is
    /// configured (the client's default host then applies).
    /// </summary>
    /// <param name="connectionString">The resolved broker connection string.</param>
    /// <returns>The URI, or <see langword="null"/>.</returns>
    /// <exception cref="InvalidOperationException">The value is not an <c>amqp://</c> or <c>amqps://</c> URI.</exception>
    internal static Uri? ParseConnectionUri(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        if (Uri.TryCreate(connectionString, UriKind.Absolute, out var uri)
            && (string.Equals(uri.Scheme, "amqp", StringComparison.OrdinalIgnoreCase) || string.Equals(uri.Scheme, "amqps", StringComparison.OrdinalIgnoreCase)))
        {
            return uri;
        }

        throw new InvalidOperationException(
            $"MessageBus:Provider is '{MessageBusProviders.RabbitMq}', but the broker connection string is not an amqp:// or amqps:// URI. "
            + "Set MessageBus:ConnectionString (or ConnectionStrings:rabbitmq) to the broker URI, e.g. amqp://user:password@host:5672/vhost.");
    }

    /// <summary>
    /// How many deliveries the receiver processes at once: <c>MessageBus:ConcurrentMessageLimit</c>, else
    /// the processor count, within RabbitMQ.Client's dispatch range.
    /// </summary>
    /// <param name="settings">The bound settings.</param>
    /// <returns>The concurrency.</returns>
    internal static ushort ResolveConcurrency(MessageBusSettings settings) =>
        (ushort)Math.Clamp(BrokerTransportPolicies.ResolveConcurrentMessageLimit(settings) ?? Environment.ProcessorCount, 1, ushort.MaxValue);

    /// <summary>
    /// The channel prefetch window: <c>MessageBus:PrefetchCount</c>, else twice the concurrency, so a
    /// finished delivery's slot is refilled without a broker round trip.
    /// </summary>
    /// <param name="settings">The bound settings.</param>
    /// <param name="concurrency">The resolved concurrency.</param>
    /// <returns>The prefetch count.</returns>
    internal static ushort ResolvePrefetchCount(MessageBusSettings settings, ushort concurrency) =>
        (ushort)Math.Clamp(BrokerTransportPolicies.ResolvePrefetchCount(settings) ?? concurrency * 2, 1, ushort.MaxValue);
}

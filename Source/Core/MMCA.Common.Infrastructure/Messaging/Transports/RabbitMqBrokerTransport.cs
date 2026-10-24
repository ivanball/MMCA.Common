using MassTransit;

namespace MMCA.Common.Infrastructure.Messaging.Transports;

/// <summary>
/// The built-in RabbitMQ transport (<c>MessageBus:Provider=RabbitMq</c>), the broker the Aspire
/// development stack and the cross-service test tier run. Written against the same
/// <see cref="IBrokerTransport"/> contract an adapter package implements.
/// </summary>
internal sealed class RabbitMqBrokerTransport : IBrokerTransport
{
    /// <inheritdoc />
    public string Name => MessageBusProviders.RabbitMq;

    /// <inheritdoc />
    public void Configure(IBusRegistrationConfigurator configurator, BrokerTransportContext context)
    {
        ArgumentNullException.ThrowIfNull(configurator);
        ArgumentNullException.ThrowIfNull(context);

        var settings = context.Settings;
        var connectionString = context.ConnectionString;

        configurator.UsingRabbitMq((busContext, cfg) =>
        {
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                cfg.Host(new Uri(connectionString));
            }

            // Delayed redelivery is opt-in here: it needs the rabbitmq_delayed_message_exchange
            // plugin, which the Aspire development container does not ship.
            BrokerTransportPolicies.ApplyReceivePolicies(cfg, settings, applyDelayedRedelivery: settings.EnableDelayedRedelivery);
            cfg.ConfigureEndpoints(busContext);
        });
    }
}

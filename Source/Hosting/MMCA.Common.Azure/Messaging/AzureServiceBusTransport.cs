using MassTransit;
using MMCA.Common.Infrastructure.Messaging.Transports;

namespace MMCA.Common.Azure.Messaging;

/// <summary>
/// The Azure Service Bus broker transport (<c>MessageBus:Provider=AzureServiceBus</c>), registered by
/// <c>AddAzureServiceBusTransport()</c> and selected by <c>AddBrokerMessaging</c>.
/// <para>
/// Second-level delayed redelivery is applied UNCONDITIONALLY: Azure Service Bus schedules messages
/// natively, so there is no plugin to install and no configuration in which it can fail at bus start.
/// <c>MessageBus:EnableDelayedRedelivery</c> is deliberately not consulted here, because it exists
/// only to gate the RabbitMQ plugin requirement.
/// </para>
/// </summary>
internal sealed class AzureServiceBusTransport : IBrokerTransport
{
    /// <summary>The <c>MessageBus:Provider</c> value that selects this transport.</summary>
    internal const string ProviderName = "AzureServiceBus";

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public void Configure(IBusRegistrationConfigurator configurator, BrokerTransportContext context)
    {
        ArgumentNullException.ThrowIfNull(configurator);
        ArgumentNullException.ThrowIfNull(context);

        var settings = context.Settings;
        var connectionString = context.ConnectionString;

        configurator.UsingAzureServiceBus((busContext, cfg) =>
        {
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                // The emulator branch exists so a development stack can run the SAME transport
                // production runs. It is entered only when the connection string carries
                // UseDevelopmentEmulator=true, a token no real namespace emits, so the production
                // path below is a single unconditional Host(connectionString).
                if (ServiceBusEmulatorSupport.IsEmulatorConnectionString(connectionString))
                {
                    ServiceBusEmulatorSupport.ConfigureEmulatorHost(
                        cfg, connectionString, settings.EmulatorAdminEndpoint);
                }
                else
                {
                    cfg.Host(connectionString);
                }
            }

            BrokerTransportPolicies.ApplyReceivePolicies(cfg, settings, applyDelayedRedelivery: true);
            cfg.ConfigureEndpoints(busContext);
        });
    }
}

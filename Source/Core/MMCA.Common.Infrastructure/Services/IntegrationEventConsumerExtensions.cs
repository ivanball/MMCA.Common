using MassTransit;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// MassTransit registration extensions for the generic <see cref="IntegrationEventConsumer{TEvent}"/>
/// adapter. Call from inside the <c>configureConsumers</c> callback passed to
/// <c>AddBrokerMessaging</c> in the host's <c>Program.cs</c>.
/// </summary>
public static class IntegrationEventConsumerExtensions
{
    extension(IBusRegistrationConfigurator x)
    {
        /// <summary>
        /// Registers a MassTransit consumer for <typeparamref name="TEvent"/> that delegates
        /// to all <see cref="MMCA.Common.Application.Interfaces.IIntegrationEventHandler{TEvent}"/>
        /// implementations resolved from DI. Use one call per integration event type the
        /// service consumes.
        /// </summary>
        /// <typeparam name="TEvent">The integration event type.</typeparam>
        public IBusRegistrationConfigurator RegisterIntegrationEventConsumer<TEvent>()
            where TEvent : class, IIntegrationEvent
        {
            x.AddConsumer<IntegrationEventConsumer<TEvent>>();
            return x;
        }
    }
}

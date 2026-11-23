using MMCA.Common.Domain.IntegrationEvents;
using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Infrastructure.Messaging.Consumers;

/// <summary>
/// Registration shorthands on <see cref="IBrokerConsumerBuilder"/>, the builder the
/// <c>configureConsumers</c> callback of <c>AddBrokerMessaging</c> receives in the host's
/// <c>Program.cs</c>.
/// </summary>
public static class IntegrationEventConsumerExtensions
{
    extension(IBrokerConsumerBuilder builder)
    {
        /// <summary>
        /// Subscribes the host to <see cref="OutputCacheEvictionRequested"/>, the framework's
        /// cross-service output-cache eviction broadcast. Shorthand for
        /// <c>RegisterIntegrationEventConsumer&lt;OutputCacheEvictionRequested&gt;()</c>, named so the
        /// wiring reads as an intention rather than a type argument.
        /// <para>
        /// Pair it with <c>services.AddOutputCacheEvictionHandler()</c> from MMCA.Common.API, which
        /// registers the handler this subscription delivers to. Subscribing without the handler is
        /// harmless but pointless: the messages complete with a "no handler registered" log and
        /// nothing is evicted.
        /// </para>
        /// </summary>
        /// <returns>The builder, for chaining.</returns>
        public IBrokerConsumerBuilder RegisterOutputCacheEvictionConsumer()
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.RegisterIntegrationEventConsumer<OutputCacheEvictionRequested>();
        }
    }
}

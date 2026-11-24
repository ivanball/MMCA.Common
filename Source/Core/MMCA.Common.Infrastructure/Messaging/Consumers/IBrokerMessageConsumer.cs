using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Infrastructure.Messaging.Consumers;

/// <summary>
/// The non-generic entry point the receive pipeline dispatches a deserialized event through, so it can
/// resolve the closed consumer for a subscription's runtime type without reflection per message.
/// Implemented explicitly by <see cref="IntegrationEventConsumer{TEvent}"/> and
/// <see cref="UpcastingIntegrationEventConsumer{TEvent}"/>, which forward to their typed
/// <c>ConsumeAsync</c>.
/// </summary>
internal interface IBrokerMessageConsumer
{
    /// <summary>Consumes one deserialized event.</summary>
    /// <param name="integrationEvent">The event, of the subscription's CLR type.</param>
    /// <param name="headers">The delivered message's headers.</param>
    /// <param name="cancellationToken">Fires when the host is stopping.</param>
    /// <returns>A task that completes when every handler has run; faults when one failed.</returns>
    Task ConsumeAsync(IIntegrationEvent integrationEvent, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken);
}

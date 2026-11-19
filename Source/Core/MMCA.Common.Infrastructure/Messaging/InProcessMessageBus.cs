using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Infrastructure.Messaging;

/// <summary>
/// <see cref="IMessageBus"/> implementation that dispatches integration events synchronously
/// through the in-process <see cref="IDomainEventDispatcher"/>. This is the default registration
/// for the modular monolith deployment.
/// <para>
/// Unlike <see cref="InProcessEventBus"/>, this bus never writes to the outbox: it is the transport
/// the <c>OutboxProcessor</c> drains already-persisted entries through, not an application publish
/// API. Application code publishes integration events through <see cref="IEventBus"/>
/// (<see cref="InProcessEventBus"/> here, <c>BrokerEventBus</c> under a broker provider).
/// </para>
/// </summary>
public sealed class InProcessMessageBus(IDomainEventDispatcher domainEventDispatcher) : IMessageBus
{
    /// <inheritdoc />
    public Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return domainEventDispatcher.DispatchAsync([integrationEvent], cancellationToken);
    }

    /// <inheritdoc />
    public Task PublishAsync(IEnumerable<IIntegrationEvent> integrationEvents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrationEvents);
        return domainEventDispatcher.DispatchAsync(integrationEvents, cancellationToken);
    }
}

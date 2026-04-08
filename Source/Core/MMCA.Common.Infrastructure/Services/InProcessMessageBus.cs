using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// <see cref="IMessageBus"/> implementation that dispatches integration events synchronously
/// through the in-process <see cref="IDomainEventDispatcher"/>. This is the default registration
/// for the modular monolith deployment.
/// <para>
/// Unlike <see cref="InProcessEventBus"/>, this bus does NOT write to the outbox — it is intended
/// to be invoked from the <c>OutboxProcessor</c> when draining already-persisted entries, or from
/// application code paths that have already taken responsibility for outbox persistence elsewhere.
/// Application code that needs the "persist + dispatch in one call" semantics should continue to
/// use <see cref="IEventBus"/> / <see cref="MMCA.Common.Application.Interfaces.IIntegrationEventPublisher"/>
/// directly until the migration to broker mode lands.
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

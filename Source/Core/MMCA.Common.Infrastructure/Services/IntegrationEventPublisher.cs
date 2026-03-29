using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// Publishes integration events by delegating to the registered <see cref="IEventBus"/>.
/// This class exists to preserve the <see cref="IIntegrationEventPublisher"/> contract
/// for callers that publish individual events. New code should prefer injecting
/// <see cref="IEventBus"/> directly.
/// </summary>
public sealed class IntegrationEventPublisher(IEventBus eventBus) : IIntegrationEventPublisher
{
    /// <inheritdoc />
    public Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default) =>
        eventBus.PublishAsync(integrationEvent, cancellationToken);
}

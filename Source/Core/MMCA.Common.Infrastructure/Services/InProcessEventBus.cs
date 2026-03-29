using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Settings;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// In-process event bus that persists integration events to the outbox and dispatches
/// them via <see cref="IDomainEventDispatcher"/>. This is the default implementation —
/// all modules run in the same process, so events are dispatched synchronously after
/// outbox persistence. The <see cref="OutboxProcessor"/> provides at-least-once retry
/// for events that fail during in-process dispatch.
/// <para>
/// To swap to an external message broker (Azure Service Bus, RabbitMQ), register an
/// alternative <see cref="IEventBus"/> implementation that publishes to the broker
/// instead of dispatching in-process.
/// </para>
/// </summary>
public sealed class InProcessEventBus(
    IDbContextFactory dbContextFactory,
    IDomainEventDispatcher domainEventDispatcher,
    IOptions<OutboxSettings> outboxOptions) : IEventBus
{
    /// <inheritdoc />
    public async Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var context = dbContextFactory.GetDbContext(outboxOptions.Value.DataSource);

        if (context.SupportsOutbox)
        {
            var outboxEntry = OutboxMessage.FromDomainEvent(integrationEvent);
            context.Set<OutboxMessage>().Add(outboxEntry);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await domainEventDispatcher.DispatchAsync([integrationEvent], cancellationToken).ConfigureAwait(false);

            outboxEntry.ProcessedOn = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await domainEventDispatcher.DispatchAsync([integrationEvent], cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PublishAsync(IEnumerable<IIntegrationEvent> integrationEvents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrationEvents);

        foreach (var integrationEvent in integrationEvents)
        {
            await PublishAsync(integrationEvent, cancellationToken).ConfigureAwait(false);
        }
    }
}

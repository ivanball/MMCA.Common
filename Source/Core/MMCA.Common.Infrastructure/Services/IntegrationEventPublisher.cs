using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Settings;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// Publishes integration events by persisting them to the outbox and dispatching them in-process.
/// Unlike domain events (which are raised on aggregate roots and dispatched during
/// <c>SaveChangesAsync</c>), integration events can be published explicitly from handlers
/// or services — e.g., a domain event handler that needs to notify other modules.
/// <para>
/// The event is added to the outbox in the current DbContext scope. If the caller is inside
/// a unit of work transaction, the outbox entry is committed atomically with aggregate changes.
/// In-process dispatch happens immediately after outbox persistence; the <c>OutboxProcessor</c>
/// provides at-least-once retry for events that fail in-process dispatch.
/// </para>
/// </summary>
public sealed class IntegrationEventPublisher(
    IDbContextFactory dbContextFactory,
    IDomainEventDispatcher domainEventDispatcher,
    IOptions<OutboxSettings> outboxOptions) : IIntegrationEventPublisher
{
    /// <inheritdoc />
    public async Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        // Persist to the outbox in the current scope's context (uses the outbox data source).
        var context = dbContextFactory.GetDbContext(outboxOptions.Value.DataSource);

        if (context.SupportsOutbox)
        {
            var outboxEntry = OutboxMessage.FromDomainEvent(integrationEvent);
            context.Set<OutboxMessage>().Add(outboxEntry);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            // Dispatch in-process first, then mark as processed.
            await domainEventDispatcher.DispatchAsync([integrationEvent], cancellationToken).ConfigureAwait(false);

            outboxEntry.ProcessedOn = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // Dispatch in-process to all registered handlers.
        await domainEventDispatcher.DispatchAsync([integrationEvent], cancellationToken).ConfigureAwait(false);
    }
}

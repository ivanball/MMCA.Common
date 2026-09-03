using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;

namespace MMCA.Common.Infrastructure.Messaging;

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
/// <param name="dbContextFactory">Scoped factory resolving the outbox target's context.</param>
/// <param name="domainEventDispatcher">Dispatches the published events in-process.</param>
/// <param name="dataSourceResolver">Resolver for the configured outbox publish target.</param>
/// <param name="outboxOptions">Configurable outbox settings supplying that target.</param>
/// <param name="timeProvider">Clock stamping <c>ProcessedOn</c> on the dispatched rows; defaults to
/// <see cref="TimeProvider.System"/> so an existing host keeps the previous constructor shape.</param>
/// <param name="messageBusOptions">Transport posture supplying <c>IsOutboxEnabled</c>. A host that
/// resolves no options (and any test constructing this type directly) keeps the outbox path, so the
/// opt-out is only ever taken because configuration asked for it.</param>
public sealed class InProcessEventBus(
    IDbContextFactory dbContextFactory,
    IDomainEventDispatcher domainEventDispatcher,
    IDataSourceResolver dataSourceResolver,
    IOptions<OutboxSettings> outboxOptions,
    TimeProvider? timeProvider = null,
    IOptions<MessageBusSettings>? messageBusOptions = null) : IEventBus
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    private readonly bool _outboxEnabled = messageBusOptions?.Value.IsOutboxEnabled ?? true;

    /// <inheritdoc />
    public async Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        await PublishBatchAsync([integrationEvent], cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PublishAsync(IEnumerable<IIntegrationEvent> integrationEvents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrationEvents);

        var events = integrationEvents as IIntegrationEvent[] ?? [.. integrationEvents];
        if (events.Length == 0)
            return;

        await PublishBatchAsync(events, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Persists all events to the outbox in a single save, dispatches them, then marks them
    /// processed with one set-based update. A dispatch failure leaves every entry in the batch
    /// unprocessed for the <see cref="OutboxProcessor"/> to retry (at-least-once delivery;
    /// handlers are idempotent via the inbox store).
    /// <para>
    /// With the outbox turned off (<c>MessageBus:EnableOutbox=false</c>, the in-process default) the
    /// direct-dispatch branch below is taken instead: no rows, no save, no processor to retry. That
    /// is the same path a context without outbox support already takes.
    /// </para>
    /// </summary>
    private async Task PublishBatchAsync(IIntegrationEvent[] events, CancellationToken cancellationToken)
    {
        var target = dataSourceResolver.ResolveLogical(outboxOptions.Value.DataSource, outboxOptions.Value.DatabaseName);
        var context = dbContextFactory.GetDbContext(target);

        if (!context.SupportsOutbox || !_outboxEnabled)
        {
            await domainEventDispatcher.DispatchAsync(events, cancellationToken).ConfigureAwait(false);
            return;
        }

        var outboxEntries = new List<OutboxMessage>(events.Length);
        foreach (var integrationEvent in events)
            outboxEntries.Add(OutboxMessage.FromDomainEvent(integrationEvent));

#pragma warning disable VSTHRD103 // EF DbSet.AddRange is intentionally synchronous (in-memory); AddRangeAsync is only for special value generators (EF guidance).
        context.Set<OutboxMessage>().AddRange(outboxEntries);
#pragma warning restore VSTHRD103
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await domainEventDispatcher.DispatchAsync(events, cancellationToken).ConfigureAwait(false);

        await OutboxFinalizer.MarkProcessedAsync(context, outboxEntries, _timeProvider, cancellationToken).ConfigureAwait(false);
    }
}

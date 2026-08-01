using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Settings;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// <see cref="IEventBus"/> implementation for microservice (broker) deployments. Persists
/// integration events to the outbox and signals the <see cref="OutboxProcessor"/> to drain
/// them, but does NOT dispatch in-process. The <c>OutboxProcessor</c> picks up the entries
/// and publishes via <see cref="MMCA.Common.Application.Messaging.IMessageBus"/>, which in
/// broker mode is wired to <see cref="BrokerMessageBus"/> → MassTransit → broker.
/// <para>
/// Replaces <see cref="InProcessEventBus"/> when <c>AddBrokerMessaging</c> is called.
/// The two implementations differ only in whether they dispatch synchronously after
/// persistence:
/// <list type="bullet">
///   <item><see cref="InProcessEventBus"/>: write outbox → dispatch in-process → mark processed.</item>
///   <item><see cref="BrokerEventBus"/>: write outbox → signal processor → return.</item>
/// </list>
/// In broker mode the in-process dispatch would be incorrect because consumers live in
/// other processes; the OutboxProcessor's broker-publish path is the only correct delivery
/// channel.
/// </para>
/// </summary>
public sealed class BrokerEventBus(
    IDbContextFactory dbContextFactory,
    IOutboxSignal outboxSignal,
    IDataSourceResolver dataSourceResolver,
    IOptions<OutboxSettings> outboxOptions) : IEventBus
{
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
    /// Persists every event of the batch to the outbox in ONE save, then wakes the processor once.
    /// The previous per-event save-and-signal loop cost a round trip per event and, worse, was not
    /// atomic: a failure partway through a batch left the earlier events committed and the rest
    /// unwritten, so a caller that saw a failure could not tell what had already been published.
    /// One save makes the batch all-or-nothing, and one signal is all the processor can consume
    /// anyway (<see cref="IOutboxSignal"/> caps at a single permit and discards the surplus).
    /// </summary>
    private async Task PublishBatchAsync(IIntegrationEvent[] events, CancellationToken cancellationToken)
    {
        var target = dataSourceResolver.ResolveLogical(outboxOptions.Value.DataSource, outboxOptions.Value.DatabaseName);
        var context = dbContextFactory.GetDbContext(target);

        if (!context.SupportsOutbox)
        {
            // No outbox support (e.g., Cosmos DB) — broker mode is incompatible with this
            // datasource. Throwing here surfaces the misconfiguration loudly rather than
            // silently dropping events.
            throw new InvalidOperationException(
                $"BrokerEventBus requires an outbox-enabled data source. The configured outbox target '{target}' (Outbox:DataSource='{outboxOptions.Value.DataSource}', Outbox:DatabaseName='{outboxOptions.Value.DatabaseName}') does not support OutboxMessage. Configure SQL Server or SQLite, or fall back to InProcessEventBus.");
        }

        var outboxEntries = new List<OutboxMessage>(events.Length);
        foreach (var integrationEvent in events)
            outboxEntries.Add(OutboxMessage.FromDomainEvent(integrationEvent));

#pragma warning disable VSTHRD103 // EF DbSet.AddRange is intentionally synchronous (in-memory); AddRangeAsync is only for special value generators (EF guidance).
        context.Set<OutboxMessage>().AddRange(outboxEntries);
#pragma warning restore VSTHRD103
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Wake the OutboxProcessor immediately so the broker publish doesn't wait for the
        // next polling cycle. The processor batches and publishes via IMessageBus.
        outboxSignal.Signal();
    }
}

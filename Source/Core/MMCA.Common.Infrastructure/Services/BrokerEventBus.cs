using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.Interfaces;
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
    IOptions<OutboxSettings> outboxOptions) : IEventBus
{
    /// <inheritdoc />
    public async Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var context = dbContextFactory.GetDbContext(outboxOptions.Value.DataSource);

        if (!context.SupportsOutbox)
        {
            // No outbox support (e.g., Cosmos DB) — broker mode is incompatible with this
            // datasource. Throwing here surfaces the misconfiguration loudly rather than
            // silently dropping events.
            throw new InvalidOperationException(
                $"BrokerEventBus requires an outbox-enabled DataSource. Current DataSource '{outboxOptions.Value.DataSource}' does not support OutboxMessage. Configure SQL Server or SQLite, or fall back to InProcessEventBus.");
        }

        var outboxEntry = OutboxMessage.FromDomainEvent(integrationEvent);
        context.Set<OutboxMessage>().Add(outboxEntry);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Wake the OutboxProcessor immediately so the broker publish doesn't wait for the
        // next polling cycle. The processor batches and publishes via IMessageBus.
        outboxSignal.Signal();
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

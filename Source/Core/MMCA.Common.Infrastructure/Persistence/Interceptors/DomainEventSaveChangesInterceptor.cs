using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Outbox;

namespace MMCA.Common.Infrastructure.Persistence.Interceptors;

/// <summary>
/// EF Core interceptor that captures domain events from aggregate roots before persistence,
/// serializes them to the outbox table atomically, and routes them after successful persistence:
/// <list type="bullet">
///   <item><b>Local domain events</b> are dispatched in-process via
///   <see cref="IDomainEventDispatcher"/> and their outbox rows marked processed; the
///   <see cref="OutboxProcessor"/> acts as a safety net if that dispatch fails.</item>
///   <item><b>Integration events</b> (<see cref="IIntegrationEvent"/>) are NOT dispatched
///   in-process: their outbox rows stay unprocessed and the <see cref="OutboxProcessor"/>
///   publishes them via <c>IMessageBus</c>, so the registered transport (in-process for the
///   monolith, broker for extracted services) determines delivery. This makes
///   <c>AddDomainEvent(integrationEvent)</c> broker-correct: before this routing, such events
///   were dispatched locally and marked processed, silently never reaching the wire.</item>
/// </list>
/// <para>
/// When the save runs inside an active transaction (the Transactional decorator path), all
/// post-save work is <b>deferred until after commit</b>: <see cref="MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.DbContextFactory"/>
/// calls <see cref="FlushDeferredAsync"/> after a successful commit and
/// <see cref="DropDeferred"/> on rollback. This keeps handler side effects (email, cache
/// writes, pushes) from acting on state that never commits, and keeps execution-strategy
/// retries from dispatching the same events once per attempt.
/// </para>
/// </summary>
/// <param name="domainEventDispatcher">Dispatches domain events to in-process handlers.</param>
/// <param name="logger">Logger for error diagnostics.</param>
/// <param name="outboxSignal">Signal to wake the outbox processor for pending rows.</param>
public sealed partial class DomainEventSaveChangesInterceptor(
    IDomainEventDispatcher domainEventDispatcher,
    ILogger<DomainEventSaveChangesInterceptor> logger,
    Outbox.IOutboxSignal outboxSignal) : SaveChangesInterceptor
{
    /// <summary>
    /// Per-context state captured before save and consumed after save.
    /// Uses <see cref="ConditionalWeakTable{TKey,TValue}"/> so state is automatically
    /// cleaned up when the context is disposed, without leaking memory.
    /// </summary>
    private static readonly ConditionalWeakTable<DbContext, CapturedState> StateTable = [];

    /// <summary>
    /// Post-save work deferred until the surrounding transaction commits, keyed by context.
    /// Entries carry the owning interceptor instance so the factory can flush through a
    /// static entry point without a DI edge back to this type.
    /// </summary>
    private static readonly ConditionalWeakTable<DbContext, List<DeferredDispatch>> DeferredTable = [];

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is ApplicationDbContext context)
            CaptureEventsAndPersistToOutbox(context);

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        if (eventData.Context is ApplicationDbContext context)
            CaptureEventsAndPersistToOutbox(context);

        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is ApplicationDbContext context)
            await DispatchAndFinalizeAsync(context, cancellationToken).ConfigureAwait(false);

        return await base.SavedChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The synchronous path cannot await the in-process dispatcher, so it relies entirely on
    /// the outbox: captured events are cleared from their aggregates (preventing the duplicate
    /// re-capture a later async save used to produce) and their rows stay unprocessed for the
    /// <see cref="OutboxProcessor"/> to deliver. Contexts without outbox support keep the
    /// legacy no-op so a later async save can still deliver their events.
    /// </remarks>
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is ApplicationDbContext { SupportsOutbox: true } context
            && StateTable.TryGetValue(context, out var state))
        {
            StateTable.Remove(context);
            ClearDomainEvents(state);

            if (state.LocalOutboxEntries.Count > 0 || state.HasIntegrationEvents)
                outboxSignal.Signal();
        }

        return base.SavedChanges(eventData, result);
    }

    /// <summary>
    /// Flushes any post-save work that was deferred while <paramref name="context"/> had an
    /// active transaction. Called by <see cref="MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.DbContextFactory"/> after a successful
    /// commit; a missed flush is safe (rows stay unprocessed and the outbox delivers them).
    /// </summary>
    internal static async Task FlushDeferredAsync(ApplicationDbContext context, CancellationToken cancellationToken)
    {
        if (!DeferredTable.TryGetValue(context, out var deferred))
            return;

        DeferredTable.Remove(context);

        foreach (var dispatch in deferred)
            await dispatch.Owner.FlushStateAsync(context, dispatch.State, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Discards post-save work deferred for <paramref name="context"/>. Called by
    /// <see cref="MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.DbContextFactory"/> on rollback: the aggregate changes and their
    /// outbox rows rolled back with the transaction, so there is nothing to deliver — and an
    /// execution-strategy retry must not flush the aborted attempt's events.
    /// </summary>
    internal static void DropDeferred(DbContext context) => DeferredTable.Remove(context);

    /// <summary>
    /// Captures domain events from aggregate roots and serializes them to the outbox table
    /// so they are persisted in the same transaction as the aggregate changes.
    /// </summary>
    private static void CaptureEventsAndPersistToOutbox(ApplicationDbContext context)
    {
        var aggregateRootEntities = context.ChangeTracker.Entries<IAggregateRoot>()
            .Where(e => e.Entity.DomainEvents is { Count: > 0 })
            .ToArray();

        if (aggregateRootEntities.Length == 0)
            return;

        var domainEvents = aggregateRootEntities
            .SelectMany(e => e.Entity.DomainEvents)
            .ToArray();

        IDomainEvent[] localEvents;
        var hasIntegrationEvents = false;
        var localOutboxEntries = new List<OutboxMessage>(domainEvents.Length);

        if (context.SupportsOutbox)
        {
            // Integration events get outbox rows but no in-process dispatch: the rows stay
            // unprocessed and the OutboxProcessor publishes them via IMessageBus. Local events
            // get rows AND fast-path in-process dispatch (rows marked processed on success).
            var locals = new List<IDomainEvent>(domainEvents.Length);
            foreach (var domainEvent in domainEvents)
            {
                var entry = OutboxMessage.FromDomainEvent(domainEvent);
#pragma warning disable VSTHRD103 // EF DbSet.Add is intentionally synchronous (in-memory); AddAsync is only for special value generators (EF guidance).
                context.Set<OutboxMessage>().Add(entry);
#pragma warning restore VSTHRD103

                if (domainEvent is IIntegrationEvent)
                {
                    hasIntegrationEvents = true;
                }
                else
                {
                    locals.Add(domainEvent);
                    localOutboxEntries.Add(entry);
                }
            }

            localEvents = [.. locals];
        }
        else
        {
            // No outbox table (e.g. Cosmos): nothing can carry integration events to the bus,
            // so keep the legacy behavior of dispatching everything in-process.
            localEvents = domainEvents;
        }

        var state = new CapturedState(aggregateRootEntities, localEvents, localOutboxEntries, hasIntegrationEvents);
        StateTable.AddOrUpdate(context, state);
    }

    /// <summary>
    /// Consumes the captured state after a successful save: defers when a transaction is
    /// active (delivery must not precede commit), otherwise flushes immediately.
    /// </summary>
    private async Task DispatchAndFinalizeAsync(ApplicationDbContext context, CancellationToken cancellationToken)
    {
        if (!StateTable.TryGetValue(context, out var state))
            return;

        StateTable.Remove(context);

        if (context.Database.CurrentTransaction is not null)
        {
            // Events are cleared NOW so a second save inside the same transaction cannot
            // re-capture them; the captured copies carry everything the flush needs.
            ClearDomainEvents(state);
            DeferredTable.GetOrCreateValue(context).Add(new DeferredDispatch(this, state));
            return;
        }

        await FlushStateAsync(context, state, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Dispatches local events in-process, marks their outbox entries processed, and signals
    /// the outbox processor for integration events (whose rows deliberately stay unprocessed).
    /// </summary>
    private async Task FlushStateAsync(ApplicationDbContext context, CapturedState state, CancellationToken cancellationToken)
    {
        try
        {
            if (state.LocalEvents.Length > 0)
                await domainEventDispatcher.DispatchAsync(state.LocalEvents, cancellationToken).ConfigureAwait(false);

            ClearDomainEvents(state);

            await OutboxFinalizer.MarkProcessedAsync(context, state.LocalOutboxEntries, cancellationToken).ConfigureAwait(false);

            if (state.HasIntegrationEvents)
                outboxSignal.Signal();
        }
        catch (Exception ex)
        {
            LogDispatchError(logger, ex);

            // In-process dispatch failed — signal the outbox processor to pick up
            // the unprocessed entries once the processing delay has elapsed.
            if (state.LocalOutboxEntries.Count > 0 || state.HasIntegrationEvents)
                outboxSignal.Signal();
        }
        finally
        {
            // Idempotent — covers the dispatch-failure path.
            ClearDomainEvents(state);
        }
    }

    private static void ClearDomainEvents(CapturedState state)
    {
        foreach (var aggregateRootEntity in state.AggregateRootEntities)
            aggregateRootEntity.Entity.ClearDomainEvents();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "In-process domain event dispatch failed; the outbox processor will retry")]
    private static partial void LogDispatchError(ILogger logger, Exception exception);

    /// <summary>Holds state captured before save that is consumed after save.</summary>
    /// <param name="AggregateRootEntities">The tracked aggregate roots whose events were captured.</param>
    /// <param name="LocalEvents">Events to dispatch in-process (excludes integration events on outbox-enabled contexts).</param>
    /// <param name="LocalOutboxEntries">Outbox rows backing <paramref name="LocalEvents"/>, marked processed after successful dispatch.</param>
    /// <param name="HasIntegrationEvents">Whether any captured event routes through the outbox to <c>IMessageBus</c>.</param>
    private sealed record CapturedState(
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<IAggregateRoot>[] AggregateRootEntities,
        IDomainEvent[] LocalEvents,
        List<OutboxMessage> LocalOutboxEntries,
        bool HasIntegrationEvents);

    /// <summary>A unit of post-commit work: the interceptor that captured it plus its state.</summary>
    private sealed record DeferredDispatch(DomainEventSaveChangesInterceptor Owner, CapturedState State);
}

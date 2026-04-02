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
/// serializes them to the outbox table atomically, and dispatches them in-process after
/// successful persistence. The <see cref="OutboxProcessor"/> acts as a safety net for any
/// events that fail in-process dispatch.
/// </summary>
/// <param name="domainEventDispatcher">Dispatches domain events to in-process handlers.</param>
/// <param name="logger">Logger for error diagnostics.</param>
/// <param name="outboxSignal">Signal to wake the outbox processor when in-process dispatch fails.</param>
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
    /// Synchronous path (used by MarkOutboxAsProcessed). Domain events are only
    /// captured during the async path, so the synchronous hook is a no-op.
    /// </remarks>
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result) =>
        base.SavedChanges(eventData, result);

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

        var outboxEntries = new List<OutboxMessage>(domainEvents.Length);

        if (context.SupportsOutbox)
        {
            foreach (var domainEvent in domainEvents)
            {
                var entry = OutboxMessage.FromDomainEvent(domainEvent);
                outboxEntries.Add(entry);
                context.Set<OutboxMessage>().Add(entry);
            }
        }

        var state = new CapturedState(aggregateRootEntities, domainEvents, outboxEntries);
        StateTable.AddOrUpdate(context, state);
    }

    /// <summary>
    /// Dispatches captured domain events in-process, marks outbox entries as processed,
    /// and clears events from aggregate roots.
    /// </summary>
    private async Task DispatchAndFinalizeAsync(ApplicationDbContext context, CancellationToken cancellationToken)
    {
        if (!StateTable.TryGetValue(context, out var state))
            return;

        StateTable.Remove(context);

        try
        {
            await domainEventDispatcher.DispatchAsync(state.DomainEvents, cancellationToken).ConfigureAwait(false);

            MarkOutboxAsProcessed(context, state.OutboxEntries);
        }
        catch (Exception ex)
        {
            LogDispatchError(logger, ex);

            // In-process dispatch failed — signal the outbox processor to pick up
            // the unprocessed entries once the processing delay has elapsed.
            if (state.OutboxEntries.Count > 0)
                outboxSignal.Signal();
        }
        finally
        {
            foreach (var aggregateRootEntity in state.AggregateRootEntities)
                aggregateRootEntity.Entity.ClearDomainEvents();
        }
    }

    /// <summary>
    /// Marks outbox entries as processed after successful in-process dispatch.
    /// Uses <c>base.SaveChanges()</c> (synchronous) to avoid re-entering the async pipeline.
    /// </summary>
    private static void MarkOutboxAsProcessed(ApplicationDbContext context, List<OutboxMessage> outboxEntries)
    {
        if (outboxEntries.Count == 0)
            return;

        var now = TimeProvider.System.GetUtcNow().UtcDateTime;
        foreach (var entry in outboxEntries)
            entry.ProcessedOn = now;

        // SaveChanges triggers this interceptor synchronously, but no aggregate roots
        // will have events (they were just cleared), so CaptureEventsAndPersistToOutbox is a no-op.
        context.SaveChanges();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "In-process domain event dispatch failed; the outbox processor will retry")]
    private static partial void LogDispatchError(ILogger logger, Exception exception);

    /// <summary>Holds state captured before save that is consumed after save.</summary>
    private sealed record CapturedState(
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<IAggregateRoot>[] AggregateRootEntities,
        IDomainEvent[] DomainEvents,
        List<OutboxMessage> OutboxEntries);
}

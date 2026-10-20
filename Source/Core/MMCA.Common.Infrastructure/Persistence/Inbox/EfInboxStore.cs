using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Outbox.Administration;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;

namespace MMCA.Common.Infrastructure.Persistence.Inbox;

/// <summary>
/// EF-backed <see cref="IInboxStore"/> that records processed messages in the consumer service's
/// own database (the configured outbox data source), so a redelivered message is skipped.
/// <para>
/// The row is STAGED at the start of the consume (<see cref="TryBeginAsync"/>) into the consume
/// scope's <see cref="ApplicationDbContext"/>, not written after the handlers finish. A handler that
/// writes through that same scoped context (services resolved from the consume scope itself)
/// commits the inbox row in the same transaction as its mutations with its own
/// <c>SaveChangesAsync</c>, which closes the window where a crash between the two reprocessed the
/// whole event. A handler built on the framework's <c>ScopedIntegrationEventHandlerBase</c> opens
/// its own DI scope per delivery and so saves through a different context instance: its mutations
/// and the inbox row are two transactions. <see cref="CompleteAsync"/> saves the row afterwards
/// whenever nothing else has, which covers that handler and an event whose handlers write nothing.
/// </para>
/// <para>
/// Atomicity holds when the handler writes to the SAME physical source this store resolves (the
/// <c>Outbox:DataSource</c>/<c>Outbox:DatabaseName</c> pair, which is the single database of a
/// monolith and of a service that owns one). A handler writing to a different physical source is
/// back to two transactions, and its inbox row is then persisted by <see cref="CompleteAsync"/>:
/// delivery stays at-least-once, which is the contract handlers are written against anyway.
/// </para>
/// <para>
/// Concurrent duplicate deliveries are absorbed by the unique index on
/// <see cref="InboxMessage.MessageId"/>, whichever save hits it: this store re-queries and treats
/// the rejection as already-processed, and a handler's own save surfaces the
/// <see cref="DbUpdateException"/> so its mutations roll back and the broker redelivers into the
/// skip path.
/// </para>
/// </summary>
public sealed partial class EfInboxStore(
    IDbContextFactory dbContextFactory,
    IDataSourceResolver dataSourceResolver,
    IOptions<OutboxSettings> outboxOptions,
    ILogger<EfInboxStore> logger,
    TimeProvider timeProvider) : IInboxStore
{
    /// <summary>
    /// Rows staged by <see cref="TryBeginAsync"/> and not yet closed out, keyed by message id. A
    /// plain dictionary rather than a concurrent one: this store is scoped per consumed message, so
    /// it holds one entry in practice and is never touched from two threads.
    /// </summary>
    private readonly Dictionary<Guid, EntityEntry<InboxMessage>> _staged = [];

    /// <inheritdoc />
    public Task<bool> AlreadyProcessedAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var context = ResolveContext();
        return context.Set<InboxMessage>()
            .AsNoTracking()
            .AnyAsync(m => m.MessageId == messageId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> TryBeginAsync(Guid messageId, string eventType, CancellationToken cancellationToken)
    {
        if (await AlreadyProcessedAsync(messageId, cancellationToken).ConfigureAwait(false))
            return false;

        _staged[messageId] = Stage(messageId, eventType);
        return true;
    }

    /// <inheritdoc />
    public async Task CompleteAsync(Guid messageId, string eventType, CancellationToken cancellationToken)
    {
        if (_staged.Remove(messageId, out var staged))
        {
            // Added still means no handler saved: persist it now.
            var state = TrackedState(staged);
            if (state == EntityState.Added)
            {
                await SaveStagedAsync(staged, messageId, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Anything else USUALLY means a handler's own SaveChangesAsync committed the row
            // atomically with its mutations. Not always: a save inside a transaction that then rolled
            // back leaves the entry Unchanged with no row, and an execution-strategy retry clears the
            // change tracker (Detached) before the handler re-runs without it. Only the database can
            // tell, so confirm, and write the row when it is absent.
            if (await AlreadyProcessedAsync(messageId, cancellationToken).ConfigureAwait(false))
                return;

            if (state != EntityState.Detached)
                staged.State = EntityState.Detached;
        }

        // No staged row (a caller that skipped TryBeginAsync, or a second CompleteAsync), or a staged
        // row that never reached the database: fall back to the write-after-handlers path so the
        // message is still recorded.
        await SaveStagedAsync(Stage(messageId, eventType), messageId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public bool Abandon(Guid messageId)
    {
        if (!_staged.Remove(messageId, out var staged))
            return true;

        var state = TrackedState(staged);
        var wasAdded = state == EntityState.Added;

        // Detach rather than leave it Added: the context is cached for the whole scope, so a
        // surviving Added row would be re-attempted by any later save on the same scope.
        if (state != EntityState.Detached)
            staged.State = EntityState.Detached;

        // Not Added means a handler's save took the row, but that save may since have rolled back
        // (or the tracker been cleared for a retry), so the row's existence is checked rather than
        // inferred before warning that the redelivery will be skipped.
        if (wasAdded || !RowCommitted(messageId))
            return true;

        // A handler committed the row before a later handler failed. The redelivery will be
        // skipped as a duplicate, so the handlers that had not run yet never will: loud, because
        // it is the one case where this design loses work a pure after-the-fact inbox retried.
        LogAbandonAfterCommit(logger, messageId);
        return false;
    }

    /// <inheritdoc />
    public Task MarkProcessedAsync(Guid messageId, string eventType, CancellationToken cancellationToken)
        => SaveStagedAsync(Stage(messageId, eventType), messageId, cancellationToken);

    private EntityEntry<InboxMessage> Stage(Guid messageId, string eventType)
    {
        var context = ResolveContext();
#pragma warning disable VSTHRD103 // EF DbSet.Add is intentionally synchronous (in-memory); AddAsync is only for special value generators (EF guidance).
        return context.Set<InboxMessage>().Add(new InboxMessage
        {
            MessageId = messageId,
            EventType = eventType,
            ProcessedOn = timeProvider.GetUtcNow().UtcDateTime,
        });
#pragma warning restore VSTHRD103
    }

    private async Task SaveStagedAsync(
        EntityEntry<InboxMessage> entry,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        var context = ResolveContext();

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // The context is cached per data source for the whole scope, so the rejected row must be
            // detached: left Added, every later SaveChangesAsync on this scope would re-attempt the
            // failed insert and fail. Same idiom as DomainEventSaveChangesInterceptor uses when it
            // discards an abandoned capture.
            entry.State = EntityState.Detached;

            // Only a concurrent duplicate delivery (the unique index on MessageId) is idempotent and
            // safe to absorb. Re-query instead of sniffing provider-specific error codes, so the check
            // holds for SQL Server and SQLite alike. Any other write failure must surface: swallowing
            // it would ACK a message whose inbox row was never written, hiding the failure from the
            // broker's redelivery.
            if (!await AlreadyProcessedAsync(messageId, cancellationToken).ConfigureAwait(false))
                throw;

            LogConcurrentDuplicate(logger, messageId);
        }
    }

    /// <summary>
    /// The staged row's state as the context tracks it NOW. Read through the context rather than
    /// the stored entry: after <c>ChangeTracker.Clear()</c> (an execution-strategy retry) the stored
    /// entry is stale and can still report Added although nothing is tracked any more.
    /// </summary>
    private static EntityState TrackedState(EntityEntry<InboxMessage> staged) =>
        staged.Context.ChangeTracker.Entries<InboxMessage>().Any(e => ReferenceEquals(e.Entity, staged.Entity))
            ? staged.State
            : EntityState.Detached;

    /// <summary>
    /// Whether the inbox row for <paramref name="messageId"/> exists in the database. Synchronous
    /// because <see cref="Abandon"/> is; it runs only on a handler-failure path. A check that itself
    /// fails answers <see langword="true"/>, the conservative reading that keeps the warning.
    /// </summary>
    private bool RowCommitted(Guid messageId)
    {
        try
        {
            return ResolveContext().Set<InboxMessage>()
                .AsNoTracking()
                .Any(m => m.MessageId == messageId);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            return true;
        }
    }

    private ApplicationDbContext ResolveContext()
    {
        var target = dataSourceResolver.ResolveLogical(outboxOptions.Value.DataSource, outboxOptions.Value.DatabaseName);
        return dbContextFactory.GetDbContext(target);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Inbox row for message {MessageId} already existed (concurrent duplicate delivery), treated as processed")]
    private static partial void LogConcurrentDuplicate(ILogger logger, Guid messageId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Integration event {MessageId} failed after a handler had already committed its inbox row; the redelivery will be skipped as a duplicate, so any handler that had not run yet will not run for this message")]
    private static partial void LogAbandonAfterCommit(ILogger logger, Guid messageId);
}

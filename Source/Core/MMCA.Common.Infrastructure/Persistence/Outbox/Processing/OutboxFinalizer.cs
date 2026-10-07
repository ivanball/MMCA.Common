using Microsoft.EntityFrameworkCore;
using MMCA.Common.Infrastructure.Persistence.DbContexts;

namespace MMCA.Common.Infrastructure.Persistence.Outbox.Processing;

/// <summary>
/// Marks outbox entries as processed after successful in-process dispatch using a single
/// set-based <c>UPDATE</c>. This bypasses the change tracker and the SaveChanges interceptor
/// pipeline entirely: one asynchronous statement instead of a full nested save, which keeps
/// the hottest write path (every event-raising command) free of synchronous database I/O.
/// Also releases the lease on those entries when the in-process dispatch fails.
/// </summary>
internal static class OutboxFinalizer
{
    /// <summary>
    /// Stamps <see cref="OutboxMessage.ProcessedOn"/> on the given entries in the database
    /// and refreshes the tracked instances so a later save does not re-issue the update.
    /// </summary>
    /// <param name="context">The context whose outbox table holds the entries.</param>
    /// <param name="outboxEntries">The entries to mark processed.</param>
    /// <param name="timeProvider">
    /// Clock stamping <c>ProcessedOn</c>. Injected like the rest of the outbox rather than read from
    /// <see cref="TimeProvider.System"/> here, so a test driving a <c>FakeTimeProvider</c> sees this
    /// stamp move with the same clock as the processor's lease, backoff and retention arithmetic.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task MarkProcessedAsync(
        ApplicationDbContext context,
        IReadOnlyList<OutboxMessage> outboxEntries,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (outboxEntries.Count == 0)
            return;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var ids = outboxEntries.Select(e => e.Id).ToArray();

        await context.Set<OutboxMessage>()
            .Where(m => ids.Contains(m.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ProcessedOn, now), cancellationToken)
            .ConfigureAwait(false);

        // ExecuteUpdate does not touch tracked instances; sync them and their snapshots so
        // the tracker stays truthful without queueing a redundant UPDATE. The original-value
        // write must precede clearing IsModified: clearing the flag reverts the current value
        // to the original, so the original must already be the new value.
        foreach (var entry in outboxEntries)
        {
            var property = context.Entry(entry).Property(nameof(OutboxMessage.ProcessedOn));
            entry.ProcessedOn = now;
            property.OriginalValue = now;
            property.IsModified = false;
        }
    }

    /// <summary>
    /// Clears the lease (<see cref="OutboxMessage.LockedUntil"/> and <see cref="OutboxMessage.LockToken"/>)
    /// on the given entries, guarded by <paramref name="lockToken"/> so a row a processor has since
    /// claimed under its own token is left alone, and refreshes the tracked instances the same way
    /// <see cref="MarkProcessedAsync"/> does.
    /// </summary>
    /// <param name="context">The context whose outbox table holds the entries.</param>
    /// <param name="outboxEntries">The entries whose lease to release.</param>
    /// <param name="lockToken">The token the entries were leased under.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task ReleaseLeaseAsync(
        ApplicationDbContext context,
        IReadOnlyList<OutboxMessage> outboxEntries,
        Guid lockToken,
        CancellationToken cancellationToken)
    {
        if (outboxEntries.Count == 0)
            return;

        var ids = outboxEntries.Select(e => e.Id).ToArray();

        await context.Set<OutboxMessage>()
            .Where(m => ids.Contains(m.Id) && m.LockToken == lockToken)
            .ExecuteUpdateAsync(
                s => s.SetProperty(m => m.LockedUntil, (DateTime?)null)
                      .SetProperty(m => m.LockToken, (Guid?)null),
                cancellationToken)
            .ConfigureAwait(false);

        foreach (var entry in outboxEntries)
        {
            var tracked = context.Entry(entry);
            var lockedUntil = tracked.Property(nameof(OutboxMessage.LockedUntil));
            var token = tracked.Property(nameof(OutboxMessage.LockToken));
            entry.LockedUntil = null;
            entry.LockToken = null;
            lockedUntil.OriginalValue = null;
            lockedUntil.IsModified = false;
            token.OriginalValue = null;
            token.IsModified = false;
        }
    }
}

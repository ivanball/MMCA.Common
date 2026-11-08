using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Infrastructure.Persistence.Retention;

/// <summary>
/// Deletes rows a page at a time for the retention sweeps. A first sweep over a long-neglected table
/// would otherwise be one enormous delete: one lock footprint, one transaction, one long statement.
/// Reading the ids and deleting by id, rather than deleting straight from a limited query, keeps it
/// on every relational engine the purges run on: all of them translate an IN list, while a limited
/// DELETE is not universally supported.
/// </summary>
internal static class BatchedDelete
{
    /// <summary>The most rows one delete statement removes.</summary>
    internal const int BatchSize = 1000;

    /// <summary>
    /// Deletes every row matching <paramref name="predicate"/>, a batch at a time, and reports the
    /// total. Cancellation is observed between batches: a stopped sweep ends normally, and the next
    /// one carries on.
    /// </summary>
    /// <typeparam name="TEntity">The row type (see <see cref="DeleteBatchesAsync"/> for what is allowed).</typeparam>
    /// <typeparam name="TOrderKey">The type of the column the sweep orders by.</typeparam>
    /// <param name="context">The context holding the table.</param>
    /// <param name="predicate">Which rows are past retention.</param>
    /// <param name="orderBy">The ordering column, which should have an index to serve it.</param>
    /// <param name="reportDeleted">
    /// Called exactly once with the rows actually removed, including when a batch throws or the
    /// sweep is cancelled mid-batch, so a failed sweep cannot lose the count of what it destroyed.
    /// </param>
    /// <param name="cancellationToken">Stops the sweep between batches.</param>
    /// <param name="batchSize">The batch size; tests pass a small one.</param>
    /// <returns>A task that completes when the sweep has ended and been reported.</returns>
    internal static async Task DeleteAsync<TEntity, TOrderKey>(
        DbContext context,
        Expression<Func<TEntity, bool>> predicate,
        Expression<Func<TEntity, TOrderKey>> orderBy,
        Action<int> reportDeleted,
        CancellationToken cancellationToken,
        int batchSize = BatchSize)
        where TEntity : class
    {
        var total = 0;
        try
        {
            await foreach (var deleted in DeleteBatchesAsync(context, predicate, orderBy, batchSize, cancellationToken).ConfigureAwait(false))
            {
                total += deleted;
            }
        }
        finally
        {
            reportDeleted(total);
        }
    }

    /// <summary>
    /// Runs the sweep, yielding the rows each delete statement removed. The row's key must be a single
    /// <see cref="Guid"/> column, as it is on every framework bookkeeping table.
    /// </summary>
    /// <typeparam name="TEntity">
    /// The row type. An <see cref="IAuditableEntity"/> is refused, because those rows are deleted by
    /// setting <c>IsDeleted</c>, and erasing one here would drop it from audit, restore and erasure
    /// accounting.
    /// </typeparam>
    /// <typeparam name="TOrderKey">The type of the column the sweep orders by.</typeparam>
    /// <param name="context">The context holding the table.</param>
    /// <param name="predicate">Which rows are past retention.</param>
    /// <param name="orderBy">The ordering column.</param>
    /// <param name="batchSize">The most rows one statement removes.</param>
    /// <param name="cancellationToken">Stops the sweep between batches.</param>
    /// <returns>The rows removed by each statement, in order.</returns>
    internal static IAsyncEnumerable<int> DeleteBatchesAsync<TEntity, TOrderKey>(
        DbContext context,
        Expression<Func<TEntity, bool>> predicate,
        Expression<Func<TEntity, TOrderKey>> orderBy,
        int batchSize,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        if (typeof(IAuditableEntity).IsAssignableFrom(typeof(TEntity)))
        {
            throw new InvalidOperationException(
                $"{typeof(TEntity).Name} is soft-deletable, so its rows must not be erased by a retention sweep.");
        }

        return RunBatchesAsync(context, predicate, orderBy, batchSize, cancellationToken);
    }

    private static async IAsyncEnumerable<int> RunBatchesAsync<TEntity, TOrderKey>(
        DbContext context,
        Expression<Func<TEntity, bool>> predicate,
        Expression<Func<TEntity, TOrderKey>> orderBy,
        int batchSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TEntity : class
    {
        var keyName = context.Model.FindEntityType(typeof(TEntity))!.FindPrimaryKey()!.Properties.Single().Name;

        while (!cancellationToken.IsCancellationRequested)
        {
            var ids = await context.Set<TEntity>().AsNoTracking()
                .Where(predicate)
                .OrderBy(orderBy)
                .Select(row => EF.Property<Guid>(row, keyName))
                .Take(batchSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (ids.Count == 0)
            {
                yield break;
            }

            yield return await context.Set<TEntity>()
                .Where(row => ids.Contains(EF.Property<Guid>(row, keyName)))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            if (ids.Count < batchSize)
            {
                yield break;
            }
        }
    }
}

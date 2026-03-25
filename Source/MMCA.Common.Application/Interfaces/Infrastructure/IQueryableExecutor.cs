namespace MMCA.Common.Application.Interfaces.Infrastructure;

/// <summary>
/// Abstracts IQueryable operations that would otherwise require a direct
/// dependency on Entity Framework Core in the Application layer.
/// </summary>
public interface IQueryableExecutor
{
    /// <summary>Eagerly loads a navigation property by its dot-separated path.</summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="query">The queryable to apply the include to.</param>
    /// <param name="navigationPropertyPath">The navigation property path (e.g. "Category" or "Order.OrderLines").</param>
    /// <returns>The queryable with the include applied.</returns>
    IQueryable<T> Include<T>(IQueryable<T> query, string navigationPropertyPath)
        where T : class;

    /// <summary>Materializes a queryable into a list asynchronously.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="query">The queryable to materialize.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The materialized list.</returns>
    Task<List<T>> ToListAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default);

    /// <summary>Returns the count of elements in the queryable asynchronously.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="query">The queryable to count.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The element count.</returns>
    Task<int> CountAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default);
}

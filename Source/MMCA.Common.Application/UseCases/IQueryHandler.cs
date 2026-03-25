namespace MMCA.Common.Application.UseCases;

/// <summary>
/// Handles a query (read operation) and returns a result. Implementations are auto-registered
/// via Scrutor and optionally wrapped by profiling decorators.
/// </summary>
/// <typeparam name="TQuery">The query type containing the read parameters.</typeparam>
/// <typeparam name="TResult">The result type.</typeparam>
public interface IQueryHandler<in TQuery, TResult>
{
    /// <summary>
    /// Executes the query.
    /// </summary>
    /// <param name="query">The query to execute.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The query result.</returns>
    Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken = default);
}

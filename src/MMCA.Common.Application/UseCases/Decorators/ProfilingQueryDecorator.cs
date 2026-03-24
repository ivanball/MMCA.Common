using StackExchange.Profiling;

namespace MMCA.Common.Application.UseCases.Decorators;

/// <summary>
/// Decorator that wraps query handler execution in a MiniProfiler step,
/// enabling performance tracing for each query type.
/// </summary>
/// <typeparam name="TQuery">The query type.</typeparam>
/// <typeparam name="TResult">The result type.</typeparam>
public sealed class ProfilingQueryDecorator<TQuery, TResult>(
    IQueryHandler<TQuery, TResult> inner) : IQueryHandler<TQuery, TResult>
{
    /// <inheritdoc />
    public async Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken = default)
    {
        using var step = MiniProfiler.Current?.Step($"QueryHandler: {typeof(TQuery).Name}");
        return await inner.HandleAsync(query, cancellationToken).ConfigureAwait(false);
    }
}

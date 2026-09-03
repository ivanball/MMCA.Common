using System.Globalization;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Markers;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.UseCases.Decorators;

/// <summary>
/// Decorator that enforces a per-query execution budget. Queries that do not implement
/// <see cref="IHasTimeout"/> pass through unchanged. When a query does, the decorator links a
/// cancellation source to the caller's token, cancels it after <see cref="IHasTimeout.Timeout"/>,
/// and converts the resulting cancellation into a failure result with
/// <see cref="ErrorType.Failure"/> and the code <c>Request.TimedOut</c>.
/// <para>
/// The framework's <see cref="ErrorType"/> taxonomy has no timeout member (it maps to HTTP status
/// codes and nothing in it corresponds to 408 or 504), so an expired budget is reported as the
/// general <see cref="ErrorType.Failure"/> classification. The machine-readable
/// <c>Request.TimedOut</c> code, not the type, is what callers branch on.
/// </para>
/// <para>
/// Registered as the innermost query decorator, so the budget covers the handler's own work only:
/// a cache hit is served before the budget is even started, and a timed-out execution returns a
/// failure that the caching decorator refuses to cache.
/// </para>
/// <para>
/// A budget of <see cref="TimeSpan.Zero"/> or less is treated as "no budget" and passes through
/// with the caller's token untouched: a misconfigured value must not fail every request instantly.
/// Cancellation raised by the CALLER's token is rethrown unchanged, so a genuinely aborted request
/// still surfaces exactly as the inner handler would; only the decorator's own budget becomes a
/// failure result.
/// </para>
/// </summary>
/// <typeparam name="TQuery">The query type.</typeparam>
/// <typeparam name="TResult">The result type (typically <see cref="Result"/> or <see cref="Result{T}"/>).</typeparam>
public sealed class TimeoutQueryDecorator<TQuery, TResult>(
    IQueryHandler<TQuery, TResult> inner) : IQueryHandler<TQuery, TResult>
{
    /// <summary>
    /// Cached delegate that creates a <typeparamref name="TResult"/> failure from a collection of
    /// <see cref="Error"/> instances. Built once per generic type instantiation via reflection
    /// to avoid per-call reflection overhead.
    /// </summary>
    /// <remarks>
    /// Built on the first short-circuit rather than in the static constructor, for the same reason
    /// as <see cref="FeatureGateQueryDecorator{TQuery,TResult}"/>:
    /// <see cref="ResultFailureFactory"/> supports only <see cref="Result"/> and
    /// <see cref="Result{T}"/>, and an eager static initializer would turn an unsupported
    /// <typeparamref name="TResult"/> into a <see cref="TypeInitializationException"/> at RESOLVE
    /// time (Scrutor's TryDecorate is unconditional) for a handler that never times out. One
    /// assignment per closed generic type; a benign duplicate build under a race produces an
    /// equivalent delegate. The happy path never touches it.
    /// </remarks>
    private static Func<IEnumerable<Error>, TResult>? _createFailure;

    /// <summary>
    /// Returns the failure factory, building it on first use. Kept static so the lazy assignment is
    /// never a write to a static field from an instance member.
    /// </summary>
    private static Func<IEnumerable<Error>, TResult> CreateFailure()
        => _createFailure ??= ResultFailureFactory.Build<TResult>();

    /// <inheritdoc />
    public async Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken = default)
    {
        if (query is not IHasTimeout hasTimeout || hasTimeout.Timeout <= TimeSpan.Zero)
            return await inner.HandleAsync(query, cancellationToken).ConfigureAwait(false);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(hasTimeout.Timeout);

        try
        {
            return await inner.HandleAsync(query, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var queryName = typeof(TQuery).Name;
            CqrsMetrics.RecordTimeout(queryName);

            var createFailure = CreateFailure();
            return createFailure([Error.Failure(
                "Request.TimedOut",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The query did not complete within its {hasTimeout.Timeout.TotalSeconds:0.###}s budget."),
                source: queryName)]);
        }
    }
}

namespace MMCA.Common.Shared.Abstractions;

/// <summary>
/// Task-returning counterparts of the <see cref="Result{T}"/> combinators, so an asynchronous
/// pipeline composes end to end without an intermediate <c>await</c> (and its temporary local)
/// between every step. Each method awaits the incoming task once, then delegates to the same
/// instance combinator, preserving its short-circuit behaviour: a failed result never runs the
/// supplied delegate.
/// </summary>
public static class ResultExtensions
{
    /// <summary>
    /// Chains an asynchronous operation onto a pending <see cref="Result{T}"/>.
    /// </summary>
    /// <typeparam name="T">The success type of the incoming result.</typeparam>
    /// <typeparam name="TOut">The success type of the bound operation.</typeparam>
    /// <param name="resultTask">The pending result to bind.</param>
    /// <param name="binder">Async function producing the next result in the chain.</param>
    /// <returns>The bound result, or the incoming errors on failure.</returns>
    public static async Task<Result<TOut>> BindAsync<T, TOut>(
        this Task<Result<T>> resultTask,
        Func<T, Task<Result<TOut>>> binder)
    {
        ArgumentNullException.ThrowIfNull(resultTask);
        ArgumentNullException.ThrowIfNull(binder);

        var result = await resultTask.ConfigureAwait(false);
        return await result.BindAsync(binder).ConfigureAwait(false);
    }

    /// <summary>
    /// Chains a synchronous result-returning operation onto a pending <see cref="Result{T}"/>.
    /// </summary>
    /// <typeparam name="T">The success type of the incoming result.</typeparam>
    /// <typeparam name="TOut">The success type of the bound operation.</typeparam>
    /// <param name="resultTask">The pending result to bind.</param>
    /// <param name="binder">Function producing the next result in the chain.</param>
    /// <returns>The bound result, or the incoming errors on failure.</returns>
    public static async Task<Result<TOut>> BindAsync<T, TOut>(
        this Task<Result<T>> resultTask,
        Func<T, Result<TOut>> binder)
    {
        ArgumentNullException.ThrowIfNull(resultTask);
        ArgumentNullException.ThrowIfNull(binder);

        var result = await resultTask.ConfigureAwait(false);
        return result.Bind(binder);
    }

    /// <summary>
    /// Transforms the success value of a pending <see cref="Result{T}"/>, or propagates its errors.
    /// </summary>
    /// <typeparam name="T">The success type of the incoming result.</typeparam>
    /// <typeparam name="TOut">The type produced by the mapping function.</typeparam>
    /// <param name="resultTask">The pending result to map.</param>
    /// <param name="mapper">Function to transform the success value.</param>
    /// <returns>A result containing the mapped value or the incoming errors.</returns>
    public static async Task<Result<TOut>> MapAsync<T, TOut>(
        this Task<Result<T>> resultTask,
        Func<T, TOut> mapper)
    {
        ArgumentNullException.ThrowIfNull(resultTask);
        ArgumentNullException.ThrowIfNull(mapper);

        var result = await resultTask.ConfigureAwait(false);
        return result.Map(mapper);
    }

    /// <summary>
    /// Runs an asynchronous side effect on the success value of a pending <see cref="Result{T}"/>
    /// and returns that same result. A failure passes through untouched.
    /// </summary>
    /// <typeparam name="T">The success type of the incoming result.</typeparam>
    /// <param name="resultTask">The pending result to tap.</param>
    /// <param name="action">Async side effect to run on the success value.</param>
    /// <returns>The awaited result, unchanged.</returns>
    public static async Task<Result<T>> TapAsync<T>(
        this Task<Result<T>> resultTask,
        Func<T, Task> action)
    {
        ArgumentNullException.ThrowIfNull(resultTask);
        ArgumentNullException.ThrowIfNull(action);

        var result = await resultTask.ConfigureAwait(false);
        if (result.IsSuccess)
        {
            await action(result.Value!).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Pattern-matches a pending <see cref="Result{T}"/>, invoking exactly one branch.
    /// </summary>
    /// <typeparam name="T">The success type of the incoming result.</typeparam>
    /// <typeparam name="TResult">The return type of both branches.</typeparam>
    /// <param name="resultTask">The pending result to match.</param>
    /// <param name="onSuccess">Function invoked with the success value.</param>
    /// <param name="onFailure">Function invoked with the error list.</param>
    /// <returns>The value produced by the selected branch.</returns>
    public static async Task<TResult> MatchAsync<T, TResult>(
        this Task<Result<T>> resultTask,
        Func<T, TResult> onSuccess,
        Func<IEnumerable<Error>, TResult> onFailure)
    {
        ArgumentNullException.ThrowIfNull(resultTask);
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);

        var result = await resultTask.ConfigureAwait(false);
        return result.Match(onSuccess, onFailure);
    }
}

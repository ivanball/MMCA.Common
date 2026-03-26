namespace MMCA.Common.Shared.Abstractions;

/// <summary>
/// Railway-oriented result type used across the entire codebase instead of exceptions for
/// expected error paths. A <see cref="Result"/> is either a success (no errors) or a failure
/// carrying one or more <see cref="Error"/> instances. Controllers convert failures to
/// RFC 9457 Problem Details responses via <c>ApiControllerBase.HandleFailure</c>.
/// </summary>
/// <remarks>
/// Use <see cref="Result{T}"/> when you need to return a value on success.
/// Use the non-generic <see cref="Result"/> for void-equivalent operations (e.g. invariant checks).
/// Combine multiple results with <see cref="Combine"/> to aggregate all errors before returning.
/// </remarks>
public class Result
{
    private readonly List<Error> _errors = [];

    /// <summary>Gets the list of errors. Empty when <see cref="IsSuccess"/> is <see langword="true"/>.</summary>
    public IReadOnlyList<Error> Errors => _errors;

    /// <summary>Gets a value indicating whether the operation succeeded (no errors).</summary>
    public bool IsSuccess => _errors.Count == 0;

    /// <summary>Gets a value indicating whether the operation failed (one or more errors).</summary>
    public bool IsFailure => _errors.Count > 0;

    /// <summary>Appends errors to this result. Used by <see cref="Result{T}"/> constructors.</summary>
    /// <param name="errors">The errors to add.</param>
    protected void AddErrors(IEnumerable<Error> errors) => _errors.AddRange(errors);

    /// <summary>Creates a successful non-generic result.</summary>
    /// <returns>A success <see cref="Result"/> with no errors.</returns>
    public static Result Success() => new();

    /// <summary>Creates a successful result carrying <paramref name="value"/>.</summary>
    /// <typeparam name="T">The type of the success value.</typeparam>
    /// <param name="value">The value to wrap.</param>
    /// <returns>A success <see cref="Result{T}"/>.</returns>
    public static Result<T> Success<T>(T value) => new(value);

    /// <summary>Creates a failed typed result from multiple errors.</summary>
    /// <typeparam name="T">The expected success type (not used, but required for type inference).</typeparam>
    /// <param name="errors">The errors describing what went wrong.</param>
    /// <returns>A failure <see cref="Result{T}"/>.</returns>
    public static Result<T> Failure<T>(IEnumerable<Error> errors) => new(errors);

    /// <summary>Creates a failed non-generic result from multiple errors.</summary>
    /// <param name="errors">The errors describing what went wrong.</param>
    /// <returns>A failure <see cref="Result"/>.</returns>
    public static Result Failure(IEnumerable<Error> errors)
    {
        var result = new Result();
        result._errors.AddRange(errors);
        return result;
    }

    /// <summary>Creates a failed non-generic result from a single error.</summary>
    /// <param name="error">The error describing what went wrong.</param>
    /// <returns>A failure <see cref="Result"/>.</returns>
    public static Result Failure(Error error) =>
        Failure([error]);

    /// <summary>Creates a failed typed result from a single error.</summary>
    /// <typeparam name="T">The expected success type.</typeparam>
    /// <param name="error">The error describing what went wrong.</param>
    /// <returns>A failure <see cref="Result{T}"/>.</returns>
    public static Result<T> Failure<T>(Error error) =>
        Failure<T>([error]);

    /// <summary>
    /// Merges multiple results into one. If all results are successful, returns success.
    /// If any are failures, returns a single failure with all errors aggregated.
    /// Commonly used to validate multiple invariants before proceeding with an operation.
    /// </summary>
    /// <param name="results">The results to combine.</param>
    /// <returns>A combined <see cref="Result"/> that is successful only if all inputs are successful.</returns>
    public static Result Combine(params ReadOnlySpan<Result> results)
    {
        if (results.Length == 0)
        {
            throw new ArgumentException("At least one result must be provided.", nameof(results));
        }

        List<Error>? allErrors = null;

        foreach (var r in results)
        {
            if (r.IsFailure)
            {
                allErrors ??= [];
                allErrors.AddRange(r._errors);
            }
        }

        return allErrors is null
            ? Success()
            : Failure(allErrors);
    }
}

/// <summary>
/// A result that carries a <typeparamref name="T"/> value on success.
/// Provides functional combinators (<see cref="Match{TResult}"/>, <see cref="Map{TOut}"/>,
/// <see cref="BindAsync{TOut}"/>) for composing operations without checking
/// <see cref="Result.IsFailure"/> at every step.
/// </summary>
/// <typeparam name="T">The type of the success value.</typeparam>
public sealed class Result<T> : Result
{
    /// <summary>Gets the success value. <see langword="null"/> when <see cref="Result.IsFailure"/> is <see langword="true"/>.</summary>
    public T? Value { get; }

    /// <summary>Initializes a new success result with the specified value.</summary>
    /// <param name="value">The success value.</param>
    internal Result(T value) => Value = value;

    /// <summary>Initializes a new failure result with the specified errors.</summary>
    /// <param name="errors">One or more errors.</param>
    internal Result(IEnumerable<Error> errors) => AddErrors(errors);

    /// <summary>
    /// Pattern-matches on the result, invoking <paramref name="onSuccess"/> with the value
    /// when successful, or <paramref name="onFailure"/> with the errors when failed.
    /// Guarantees exactly one branch is executed.
    /// </summary>
    /// <typeparam name="TResult">The return type of both branches.</typeparam>
    /// <param name="onSuccess">Function invoked with the success value.</param>
    /// <param name="onFailure">Function invoked with the error list.</param>
    /// <returns>The value produced by the selected branch.</returns>
    public TResult Match<TResult>(Func<T, TResult> onSuccess, Func<IEnumerable<Error>, TResult> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);

        return IsFailure
            ? onFailure(Errors)
            : onSuccess(Value!);
    }

    /// <summary>
    /// Transforms the success value using <paramref name="mapper"/>, or propagates errors unchanged.
    /// </summary>
    /// <typeparam name="TOut">The type produced by the mapping function.</typeparam>
    /// <param name="mapper">Function to transform the success value.</param>
    /// <returns>A new result containing the mapped value or the original errors.</returns>
    public Result<TOut> Map<TOut>(Func<T, TOut> mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        return IsFailure ? Result.Failure<TOut>(Errors) : Result.Success(mapper(Value!));
    }

    /// <summary>
    /// Chains an asynchronous operation that itself returns a <see cref="Result{TOut}"/>.
    /// Short-circuits on failure, propagating errors without invoking <paramref name="binder"/>.
    /// </summary>
    /// <typeparam name="TOut">The success type of the bound operation.</typeparam>
    /// <param name="binder">Async function producing the next result in the chain.</param>
    /// <returns>The result of the bound operation, or the original errors on failure.</returns>
    public async Task<Result<TOut>> BindAsync<TOut>(Func<T, Task<Result<TOut>>> binder)
    {
        ArgumentNullException.ThrowIfNull(binder);
        return IsFailure ? Result.Failure<TOut>(Errors) : await binder(Value!).ConfigureAwait(false);
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using MMCA.Common.Shared.Serialization;

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
[JsonConverter(typeof(ResultJsonConverterFactory))]
public class Result
{
    private static readonly Result CachedSuccess = new();
    private static readonly Error[] NoErrors = [];

    // Lazily allocated: the success path (the overwhelming majority of Results created
    // per request) never pays for an error list allocation.
    private List<Error>? _errors;

    /// <summary>Gets the list of errors. Empty when <see cref="IsSuccess"/> is <see langword="true"/>.</summary>
    public IReadOnlyList<Error> Errors => _errors ?? (IReadOnlyList<Error>)NoErrors;

    /// <summary>Gets a value indicating whether the operation succeeded (no errors).</summary>
    public bool IsSuccess => _errors is null || _errors.Count == 0;

    /// <summary>Gets a value indicating whether the operation failed (one or more errors).</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>
    /// Implicitly lifts a single <see cref="Error"/> into a failed <see cref="Result"/>, so a
    /// guard clause can <c>return someError;</c> without naming the factory.
    /// </summary>
    /// <param name="error">The error describing what went wrong.</param>
    /// <returns>A failure <see cref="Result"/> carrying <paramref name="error"/>.</returns>
    public static implicit operator Result(Error error) => FromError(error);

    /// <summary>Named alternate for the <see cref="Error"/> to <see cref="Result"/> implicit conversion.</summary>
    /// <param name="error">The error describing what went wrong.</param>
    /// <returns>A failure <see cref="Result"/> carrying <paramref name="error"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="error"/> is <see langword="null"/>.</exception>
    public static Result FromError(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return Failure(error);
    }

    /// <summary>Appends errors to this result. Used by <see cref="Result{T}"/> constructors.</summary>
    /// <param name="errors">The errors to add.</param>
    protected void AddErrors(IEnumerable<Error> errors) => (_errors ??= []).AddRange(errors);

    /// <summary>Creates a successful non-generic result.</summary>
    /// <remarks>Returns a shared immutable instance; success results carry no per-instance state.</remarks>
    /// <returns>A success <see cref="Result"/> with no errors.</returns>
    public static Result Success() => CachedSuccess;

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
    /// <param name="errors">The errors describing what went wrong. Must contain at least one error.</param>
    /// <returns>A failure <see cref="Result"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="errors"/> is empty:
    /// with <see cref="IsSuccess"/> derived from the error count, an accidentally-empty
    /// collection would otherwise produce a <em>success</em> from a call that explicitly
    /// asked for a failure.</exception>
    public static Result Failure(IEnumerable<Error> errors)
    {
        var result = new Result();
        result.AddErrors(errors);
        ThrowIfNoErrors(result);
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

    /// <summary>Guards the failure factories against empty error collections.</summary>
    /// <param name="result">The result that was just built from a caller-supplied error collection.</param>
    /// <exception cref="ArgumentException">Thrown when the built result carries no errors.</exception>
    private protected static void ThrowIfNoErrors(Result result)
    {
        if (result.IsSuccess)
        {
            throw new ArgumentException(
                "A failure Result requires at least one error; an empty error collection would silently produce a success.",
                nameof(result));
        }
    }

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
                allErrors.AddRange(r.Errors);
            }
        }

        return allErrors is null
            ? Success()
            : Failure(allErrors);
    }

    /// <summary>
    /// Pattern-matches on a valueless result, invoking <paramref name="onSuccess"/> when successful
    /// or <paramref name="onFailure"/> with the errors when failed. Exactly one branch runs.
    /// </summary>
    /// <typeparam name="TResult">The return type of both branches.</typeparam>
    /// <param name="onSuccess">Function invoked when the result succeeded.</param>
    /// <param name="onFailure">Function invoked with the error list when the result failed.</param>
    /// <returns>The value produced by the selected branch.</returns>
    public TResult Match<TResult>(Func<TResult> onSuccess, Func<IEnumerable<Error>, TResult> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);

        return IsFailure ? onFailure(Errors) : onSuccess();
    }

    /// <summary>
    /// Runs <paramref name="action"/> with the errors when this result is a failure, then returns
    /// the same instance so the call can sit inline in a chain. A success is passed through untouched.
    /// </summary>
    /// <param name="action">Side effect to run on the error list.</param>
    /// <returns>This same <see cref="Result"/> instance.</returns>
    public Result OnFailure(Action<IReadOnlyList<Error>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (IsFailure)
        {
            action(Errors);
        }

        return this;
    }

    /// <summary>
    /// Chains a valueless operation, short-circuiting on failure so <paramref name="binder"/>
    /// never runs against a broken state.
    /// </summary>
    /// <param name="binder">Function producing the next result in the chain.</param>
    /// <returns>The bound result, or this result's errors unchanged.</returns>
    public Result Bind(Func<Result> binder)
    {
        ArgumentNullException.ThrowIfNull(binder);
        return IsFailure ? this : binder();
    }
}

/// <summary>
/// A result that carries a <typeparamref name="T"/> value on success.
/// Provides functional combinators (<see cref="Match{TResult}"/>, <see cref="MatchAsync{TResult}"/>,
/// <see cref="Map{TOut}"/>, <see cref="Bind{TOut}"/>, <see cref="BindAsync{TOut}"/>,
/// <see cref="Tap"/>, <see cref="Ensure"/>) for composing operations without checking
/// <see cref="Result.IsFailure"/> at every step. <see cref="ResultExtensions"/> carries the
/// same combinators over a pending <see cref="Task{TResult}"/> of results.
/// </summary>
/// <typeparam name="T">The type of the success value.</typeparam>
[JsonConverter(typeof(ResultJsonConverterFactory))]
public sealed class Result<T> : Result
{
    /// <summary>Gets the success value. <see langword="null"/> when <see cref="Result.IsFailure"/> is <see langword="true"/>.</summary>
    public T? Value { get; }

    /// <summary>Initializes a new success result with the specified value.</summary>
    /// <param name="value">The success value.</param>
    internal Result(T value) => Value = value;

    /// <summary>Initializes a new failure result with the specified errors.</summary>
    /// <param name="errors">One or more errors. Must contain at least one error.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="errors"/> is empty
    /// (an empty collection would silently produce a success carrying a null value).</exception>
    internal Result(IEnumerable<Error> errors)
    {
        AddErrors(errors);
        ThrowIfNoErrors(this);
    }

    /// <summary>
    /// Implicitly lifts a single <see cref="Error"/> into a failed <see cref="Result{T}"/>,
    /// so a guard clause can <c>return someError;</c> without naming the factory.
    /// </summary>
    /// <param name="error">The error describing what went wrong.</param>
    /// <returns>A failure <see cref="Result{T}"/> carrying <paramref name="error"/>.</returns>
    [SuppressMessage(
        "Usage",
        "CA2225:Operator overloads have named alternates",
        Justification = "The inherited static factory Result.Failure<T>(Error) is the named alternate; a 'FromError' static on the generic type would trip CA1000.")]
    public static implicit operator Result<T>(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return Failure<T>(error);
    }

    /// <summary>
    /// Implicitly lifts a value into a successful <see cref="Result{T}"/>, so a handler can
    /// <c>return theValue;</c> on the happy path.
    /// </summary>
    /// <param name="value">The success value to wrap.</param>
    /// <returns>A success <see cref="Result{T}"/> carrying <paramref name="value"/>.</returns>
    [SuppressMessage(
        "Usage",
        "CA2225:Operator overloads have named alternates",
        Justification = "The inherited static factory Result.Success<T>(T) is the named alternate; the rule's suggested 'FromT' name is meaningless for an unconstrained type parameter.")]
    public static implicit operator Result<T>(T value) => Success(value);

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

    /// <summary>
    /// Chains a synchronous operation that itself returns a <see cref="Result{TOut}"/>.
    /// Short-circuits on failure, propagating errors without invoking <paramref name="binder"/>.
    /// </summary>
    /// <typeparam name="TOut">The success type of the bound operation.</typeparam>
    /// <param name="binder">Function producing the next result in the chain.</param>
    /// <returns>The result of the bound operation, or the original errors on failure.</returns>
    public Result<TOut> Bind<TOut>(Func<T, Result<TOut>> binder)
    {
        ArgumentNullException.ThrowIfNull(binder);
        return IsFailure ? Result.Failure<TOut>(Errors) : binder(Value!);
    }

    /// <summary>
    /// Runs <paramref name="action"/> with the success value, then returns the same instance so
    /// the call can sit inline in a chain. A failure is passed through untouched.
    /// </summary>
    /// <param name="action">Side effect to run on the success value.</param>
    /// <returns>This same <see cref="Result{T}"/> instance.</returns>
    public Result<T> Tap(Action<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (IsSuccess)
        {
            action(Value!);
        }

        return this;
    }

    /// <summary>
    /// Fails the chain with <paramref name="error"/> when the success value does not satisfy
    /// <paramref name="predicate"/>. An already-failed result is returned unchanged and the
    /// predicate never runs.
    /// </summary>
    /// <param name="predicate">Condition the success value must satisfy.</param>
    /// <param name="error">The error to fail with when the predicate does not hold.</param>
    /// <returns>This result when it succeeds and the predicate holds, otherwise a failure.</returns>
    public Result<T> Ensure(Func<T, bool> predicate, Error error)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(error);

        if (IsFailure)
        {
            return this;
        }

        return predicate(Value!) ? this : Result.Failure<T>(error);
    }

    /// <summary>
    /// Asynchronous counterpart of <see cref="Match{TResult}"/>: awaits whichever branch applies.
    /// Guarantees exactly one branch is executed.
    /// </summary>
    /// <typeparam name="TResult">The return type of both branches.</typeparam>
    /// <param name="onSuccess">Async function invoked with the success value.</param>
    /// <param name="onFailure">Async function invoked with the error list.</param>
    /// <returns>The value produced by the selected branch.</returns>
    public async Task<TResult> MatchAsync<TResult>(
        Func<T, Task<TResult>> onSuccess,
        Func<IEnumerable<Error>, Task<TResult>> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);

        return IsFailure
            ? await onFailure(Errors).ConfigureAwait(false)
            : await onSuccess(Value!).ConfigureAwait(false);
    }
}

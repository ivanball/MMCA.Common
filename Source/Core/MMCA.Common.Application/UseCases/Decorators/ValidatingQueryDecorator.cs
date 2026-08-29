using FluentValidation;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Extensions;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.UseCases.Decorators;

/// <summary>
/// Decorator that automatically validates the query using EVERY registered
/// <see cref="IValidator{T}"/> before invoking the inner handler. If any of them fails, the handler
/// is never called and a <see cref="Result"/> failure containing the union of their validation
/// errors is returned immediately.
/// <para>
/// The query twin of <see cref="ValidatingCommandDecorator{TCommand, TResult}"/>: same validator
/// resolution (every registered <c>IValidator&lt;TQuery&gt;</c> runs, in registration order), same
/// error aggregation, same pass-through when a query has no validator at all. Queries carrying
/// paging, filter or sort input therefore reject a malformed request the same way commands do,
/// instead of pushing the bad values into the data source.
/// </para>
/// <para>
/// <b>Pipeline placement (ADR-014):</b> registered between
/// <see cref="CachingQueryDecorator{TQuery, TResult}"/> and
/// <see cref="TimeoutQueryDecorator{TQuery, TResult}"/>, so execution runs
/// FeatureGate, Authorization, Logging, Caching, <b>Validating</b>, Timeout, then the handler.
/// Validation deliberately sits <b>inside</b> caching rather than outside it: a cached entry can
/// only exist because the same query already passed validation when the entry was first produced,
/// so re-validating on a cache hit spends work to reach a conclusion already reached. It sits
/// outside the timeout for the mirror of the command-side reason: the caller is not charged a slice
/// of the execution budget for validating its own bad input.
/// </para>
/// </summary>
/// <typeparam name="TQuery">The query type to validate.</typeparam>
/// <typeparam name="TResult">The result type (typically <see cref="Result"/> or <see cref="Result{T}"/>).</typeparam>
public sealed partial class ValidatingQueryDecorator<TQuery, TResult>(
    IQueryHandler<TQuery, TResult> inner,
    IEnumerable<IValidator<TQuery>> validators,
    ILogger<ValidatingQueryDecorator<TQuery, TResult>> logger) : IQueryHandler<TQuery, TResult>
{
    private readonly IValidator<TQuery>[] _validators = [.. validators];

    /// <summary>
    /// Cached delegate that creates a <typeparamref name="TResult"/> failure from a collection of
    /// <see cref="Error"/> instances. Built once per generic type instantiation via reflection
    /// to avoid per-call reflection overhead.
    /// </summary>
    /// <remarks>
    /// Built on the first short-circuit rather than in the static constructor, for the same reason
    /// as <see cref="ValidatingCommandDecorator{TCommand, TResult}"/>:
    /// <see cref="ResultFailureFactory"/> supports only <see cref="Result"/> and
    /// <see cref="Result{T}"/>, and an eager static initializer would turn an unsupported
    /// <typeparamref name="TResult"/> into a <see cref="TypeInitializationException"/> at RESOLVE
    /// time (Scrutor's TryDecorate is unconditional) for a query that never fails validation. One
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
        if (_validators.Length == 0)
        {
            return await inner.HandleAsync(query, cancellationToken).ConfigureAwait(false);
        }

        // Sequential for the same reason as the command decorator: a validator may read through a
        // scoped repository, and a DbContext is not thread-safe.
        List<Error>? errors = null;
        foreach (var validator in _validators)
        {
            var validationResult = await validator.ValidateAsync(query, cancellationToken).ConfigureAwait(false);
            if (validationResult.IsValid)
            {
                continue;
            }

            errors ??= [];
            errors.AddRange(validationResult.ToErrors(typeof(TQuery).Name));
        }

        if (errors is null)
        {
            return await inner.HandleAsync(query, cancellationToken).ConfigureAwait(false);
        }

        LogValidationFailure(errors);

        var createFailure = CreateFailure();
        return createFailure(errors);
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Validation failed for query '{QueryName}' with {ErrorCount} error(s)")]
    private static partial void LogValidationFailure(
        ILogger logger,
        string queryName,
        int errorCount);

    private void LogValidationFailure(List<Error> errors) =>
        LogValidationFailure(logger, typeof(TQuery).Name, errors.Count);
}

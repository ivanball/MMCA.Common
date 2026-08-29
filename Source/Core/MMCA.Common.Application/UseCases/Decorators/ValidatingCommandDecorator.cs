using FluentValidation;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Extensions;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.UseCases.Decorators;

/// <summary>
/// Decorator that automatically validates the command using EVERY registered
/// <see cref="IValidator{T}"/> before invoking the inner handler. If any of them fails, the handler
/// is never called and a <see cref="Result"/> failure containing the union of their validation
/// errors is returned immediately.
/// <para>
/// This eliminates the need for handlers to inject and call <see cref="IValidator{T}"/> manually.
/// Commands without a registered validator pass through to the handler unchanged.
/// </para>
/// <para>
/// All registered validators run, not just the first one: a command commonly carries a
/// module-authored validator beside a framework or cross-cutting one, and honoring only the first
/// registration turns the others into dead code whose rules are silently unenforced. Running them
/// all also means the caller sees every broken rule in one response instead of one per round trip.
/// </para>
/// <para>
/// Placed between <see cref="CachingCommandDecorator{TCommand, TResult}"/> and
/// <see cref="TransactionalCommandDecorator{TCommand, TResult}"/> in the decorator pipeline
/// so that invalid commands short-circuit before a database transaction is started.
/// </para>
/// </summary>
/// <typeparam name="TCommand">The command type to validate.</typeparam>
/// <typeparam name="TResult">The result type (typically <see cref="Result"/> or <see cref="Result{T}"/>).</typeparam>
public sealed partial class ValidatingCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    IEnumerable<IValidator<TCommand>> validators,
    ILogger<ValidatingCommandDecorator<TCommand, TResult>> logger) : ICommandHandler<TCommand, TResult>
{
    private readonly IValidator<TCommand>[] _validators = [.. validators];

    /// <summary>
    /// Cached delegate that creates a <typeparamref name="TResult"/> failure from a collection of
    /// <see cref="Error"/> instances. Built once per generic type instantiation via reflection
    /// to avoid per-call reflection overhead.
    /// </summary>
    /// <remarks>
    /// Built on the first short-circuit rather than in the static constructor:
    /// <see cref="ResultFailureFactory"/> supports only <see cref="Result"/> and
    /// <see cref="Result{T}"/>, and an eager static initializer turned an unsupported
    /// <typeparamref name="TResult"/> into a <see cref="TypeInitializationException"/> at RESOLVE
    /// time (Scrutor's TryDecorate is unconditional) for a handler that never short-circuits. One
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
    public async Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken = default)
    {
        if (_validators.Length == 0)
        {
            return await inner.HandleAsync(command, cancellationToken).ConfigureAwait(false);
        }

        // Sequential on purpose: a validator is free to reach the database through a scoped
        // repository, and a DbContext is not thread-safe, so running the set concurrently would
        // trade a correctness guarantee for a saving measured in microseconds.
        List<Error>? errors = null;
        foreach (var validator in _validators)
        {
            var validationResult = await validator.ValidateAsync(command, cancellationToken).ConfigureAwait(false);
            if (validationResult.IsValid)
            {
                continue;
            }

            errors ??= [];
            errors.AddRange(validationResult.ToErrors(typeof(TCommand).Name));
        }

        if (errors is null)
        {
            return await inner.HandleAsync(command, cancellationToken).ConfigureAwait(false);
        }

        LogValidationFailure(errors);

        var createFailure = CreateFailure();
        return createFailure(errors);
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Validation failed for command '{CommandName}' with {ErrorCount} error(s)")]
    private static partial void LogValidationFailure(
        ILogger logger,
        string commandName,
        int errorCount);

    private void LogValidationFailure(List<Error> errors) =>
        LogValidationFailure(logger, typeof(TCommand).Name, errors.Count);
}

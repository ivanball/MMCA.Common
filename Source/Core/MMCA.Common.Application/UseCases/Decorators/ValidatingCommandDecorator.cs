using FluentValidation;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Extensions;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.UseCases.Decorators;

/// <summary>
/// Decorator that automatically validates the command using a registered <see cref="IValidator{T}"/>
/// before invoking the inner handler. If validation fails, the handler is never called and a
/// <see cref="Result"/> failure containing all validation errors is returned immediately.
/// <para>
/// This eliminates the need for handlers to inject and call <see cref="IValidator{T}"/> manually.
/// Commands without a registered validator pass through to the handler unchanged.
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
    private readonly IValidator<TCommand>? _validator = validators.FirstOrDefault();

    /// <summary>
    /// Cached delegate that creates a <typeparamref name="TResult"/> failure from a collection of
    /// <see cref="Error"/> instances. Built once per generic type instantiation via reflection
    /// to avoid per-call reflection overhead.
    /// </summary>
    private static readonly Func<IEnumerable<Error>, TResult> CreateFailure = BuildFailureFactory();

    /// <inheritdoc />
    public async Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken = default)
    {
        if (_validator is null)
        {
            return await inner.HandleAsync(command, cancellationToken);
        }

        var validationResult = await _validator.ValidateAsync(command, cancellationToken);
        if (validationResult.IsValid)
        {
            return await inner.HandleAsync(command, cancellationToken);
        }

        var errors = validationResult.ToErrors(typeof(TCommand).Name).ToList();
        LogValidationFailure(errors);

        return CreateFailure(errors);
    }

    /// <summary>
    /// Builds a delegate that creates a <typeparamref name="TResult"/> failure.
    /// Handles both non-generic <see cref="Result"/> and generic <see cref="Result{T}"/>.
    /// </summary>
    private static Func<IEnumerable<Error>, TResult> BuildFailureFactory()
    {
        if (typeof(TResult) == typeof(Result))
        {
            return errors => (TResult)(object)Result.Failure(errors);
        }

        if (typeof(TResult).IsGenericType && typeof(TResult).GetGenericTypeDefinition() == typeof(Result<>))
        {
            var innerType = typeof(TResult).GetGenericArguments()[0];
            var failureMethod = typeof(Result)
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .First(m => m.Name == nameof(Result.Failure)
                         && m.IsGenericMethodDefinition
                         && m.GetParameters().Length == 1
                         && m.GetParameters()[0].ParameterType == typeof(IEnumerable<Error>))
                .MakeGenericMethod(innerType);

            return errors => (TResult)failureMethod.Invoke(null, [errors])!;
        }

        throw new InvalidOperationException(
            $"ValidatingCommandDecorator does not support TResult type '{typeof(TResult).FullName}'. " +
            $"Expected Result or Result<T>.");
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

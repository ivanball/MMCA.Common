using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.UseCases.Decorators;

/// <summary>
/// Builds cached delegates that create <see cref="Result"/> or <see cref="Result{T}"/> failures
/// from error collections. Used by decorator classes that need to short-circuit the handler pipeline
/// with a failure result without invoking the inner handler.
/// </summary>
internal static class ResultFailureFactory
{
    /// <summary>
    /// Builds a delegate that creates a <typeparamref name="TResult"/> failure.
    /// Handles both non-generic <see cref="Result"/> and generic <see cref="Result{T}"/>.
    /// </summary>
    /// <typeparam name="TResult">The result type (typically <see cref="Result"/> or <see cref="Result{T}"/>).</typeparam>
    internal static Func<IEnumerable<Error>, TResult> Build<TResult>()
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
            $"ResultFailureFactory does not support TResult type '{typeof(TResult).FullName}'. " +
            $"Expected Result or Result<T>.");
    }
}

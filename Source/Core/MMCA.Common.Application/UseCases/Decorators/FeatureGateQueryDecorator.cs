using Microsoft.FeatureManagement;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.UseCases.Decorators;

/// <summary>
/// Decorator that checks whether the query's feature flag is enabled before
/// executing the inner handler. Queries that do not implement <see cref="IFeatureGated"/>
/// pass through unchanged. When the feature is disabled, returns a failure result
/// with <see cref="ErrorType.NotFound"/> without invoking the handler.
/// <para>
/// Registered as the outermost standard decorator so that disabled features are
/// rejected immediately — before logging or caching work.
/// </para>
/// </summary>
/// <typeparam name="TQuery">The query type.</typeparam>
/// <typeparam name="TResult">The result type (typically <see cref="Result"/> or <see cref="Result{T}"/>).</typeparam>
public sealed class FeatureGateQueryDecorator<TQuery, TResult>(
    IQueryHandler<TQuery, TResult> inner,
    IFeatureManager featureManager) : IQueryHandler<TQuery, TResult>
{
    /// <summary>
    /// Cached delegate that creates a <typeparamref name="TResult"/> failure from a collection of
    /// <see cref="Error"/> instances. Built once per generic type instantiation via reflection
    /// to avoid per-call reflection overhead.
    /// </summary>
    private static readonly Func<IEnumerable<Error>, TResult> CreateFailure = BuildFailureFactory();

    /// <inheritdoc />
    public async Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken = default)
    {
        if (query is not IFeatureGated featureGated)
            return await inner.HandleAsync(query, cancellationToken).ConfigureAwait(false);

        if (await featureManager.IsEnabledAsync(featureGated.FeatureName).ConfigureAwait(false))
            return await inner.HandleAsync(query, cancellationToken).ConfigureAwait(false);

        return CreateFailure([Error.NotFoundError(
            "Feature.Disabled",
            $"Feature '{featureGated.FeatureName}' is not currently available.")]);
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
            $"FeatureGateQueryDecorator does not support TResult type '{typeof(TResult).FullName}'. " +
            $"Expected Result or Result<T>.");
    }
}

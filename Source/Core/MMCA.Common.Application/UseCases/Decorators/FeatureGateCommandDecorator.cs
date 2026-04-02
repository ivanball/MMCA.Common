using Microsoft.FeatureManagement;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.UseCases.Decorators;

/// <summary>
/// Decorator that checks whether the command's feature flag is enabled before
/// executing the inner handler. Commands that do not implement <see cref="IFeatureGated"/>
/// pass through unchanged. When the feature is disabled, returns a failure result
/// with <see cref="ErrorType.NotFound"/> without invoking the handler.
/// <para>
/// Registered as the outermost standard decorator so that disabled features are
/// rejected immediately — before logging, caching, validation, or transaction work.
/// </para>
/// </summary>
/// <typeparam name="TCommand">The command type.</typeparam>
/// <typeparam name="TResult">The result type (typically <see cref="Result"/> or <see cref="Result{T}"/>).</typeparam>
public sealed class FeatureGateCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    IFeatureManager featureManager) : ICommandHandler<TCommand, TResult>
{
    /// <summary>
    /// Cached delegate that creates a <typeparamref name="TResult"/> failure from a collection of
    /// <see cref="Error"/> instances. Built once per generic type instantiation via reflection
    /// to avoid per-call reflection overhead.
    /// </summary>
    private static readonly Func<IEnumerable<Error>, TResult> CreateFailure = ResultFailureFactory.Build<TResult>();

    /// <inheritdoc />
    public async Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken = default)
    {
        if (command is not IFeatureGated featureGated)
            return await inner.HandleAsync(command, cancellationToken).ConfigureAwait(false);

        if (await featureManager.IsEnabledAsync(featureGated.FeatureName).ConfigureAwait(false))
            return await inner.HandleAsync(command, cancellationToken).ConfigureAwait(false);

        return CreateFailure([Error.NotFoundError(
            "Feature.Disabled",
            $"Feature '{featureGated.FeatureName}' is not currently available.")]);
    }
}

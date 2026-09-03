using Microsoft.FeatureManagement;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Markers;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.UseCases.Decorators;

/// <summary>
/// Decorator that checks whether the command's feature flag is enabled before
/// executing the inner handler. Commands that do not implement <see cref="IFeatureGated"/>
/// pass through unchanged. When the feature is disabled, returns a failure result
/// with <see cref="ErrorType.NotFound"/> without invoking the handler.
/// <para>
/// Registered as the outermost standard decorator so that disabled features are
/// rejected immediately, before logging, caching, validation, or transaction work.
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
        if (command is not IFeatureGated featureGated)
            return await inner.HandleAsync(command, cancellationToken).ConfigureAwait(false);

        if (await featureManager.IsEnabledAsync(featureGated.FeatureName).ConfigureAwait(false))
            return await inner.HandleAsync(command, cancellationToken).ConfigureAwait(false);

        var createFailure = CreateFailure();
        return createFailure([Error.NotFoundError(
            "Feature.Disabled",
            $"Feature '{featureGated.FeatureName}' is not currently available.")]);
    }
}

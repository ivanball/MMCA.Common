namespace MMCA.Common.Application.UseCases;

/// <summary>
/// Marker interface for commands and queries that are gated behind a feature flag.
/// The <see cref="Decorators.FeatureGateCommandDecorator{TCommand,TResult}"/> and
/// <see cref="Decorators.FeatureGateQueryDecorator{TQuery,TResult}"/> check
/// <see cref="FeatureName"/> via <c>IFeatureManager.IsEnabledAsync</c> and
/// short-circuit with a failure result when the feature is disabled.
/// </summary>
public interface IFeatureGated
{
    /// <summary>
    /// The feature flag name to check (e.g. "Catalog.ProductReviews").
    /// Must match a key in the "FeatureManagement" configuration section.
    /// </summary>
    string FeatureName { get; }
}

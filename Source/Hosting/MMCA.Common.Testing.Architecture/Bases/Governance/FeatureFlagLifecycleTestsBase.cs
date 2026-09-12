namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Feature-flag lifecycle fitness functions (ADR-031): every flag constant declares whether it is a
/// permanent capability switch or a temporary rollout toggle, and a temporary one fails the build
/// once its <c>RemoveBy</c> date has passed. The second rule is the dead-toggle detector the
/// framework previously recorded as an open trade-off: a flag whose rollout finished stops being a
/// choice and becomes a branch nobody takes, and nothing else in a codebase notices.
/// </summary>
public abstract class FeatureFlagLifecycleTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// The date the expiry rule judges against. Override to freeze it (a release date, say) rather
    /// than let the build's own clock decide when a toggle goes red.
    /// </summary>
    protected virtual DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    [Fact]
    public void FeatureFlags_ShouldDeclare_Lifetime() => ArchitectureRules.FeatureFlagsDeclareLifetime(Map);

    [Fact]
    public void TemporaryFeatureFlags_ShouldNotBe_PastRemoveBy() =>
        ArchitectureRules.TemporaryFeatureFlagsAreNotPastRemoveBy(Map, Today);
}

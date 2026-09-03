namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Opt-in fitness functions for the Specification pattern in polyglot / database-per-service repos.
/// A repo that stores related entities in different physical data sources subclasses this (supplying
/// its <see cref="IArchitectureMap"/>) to guarantee no specification filters by navigating to another
/// entity — which would not translate when that entity lives in a different source. Single-engine
/// repos need not subclass it.
/// </summary>
public abstract class SpecificationConventionTestsBase
{
    /// <summary>The repo's architecture map.</summary>
    protected abstract IArchitectureMap Map { get; }

    [Fact]
    public void Specifications_ShouldNotNavigate_ToOtherEntities() =>
        ArchitectureRules.SpecificationsDoNotNavigateToOtherEntities(Map);
}

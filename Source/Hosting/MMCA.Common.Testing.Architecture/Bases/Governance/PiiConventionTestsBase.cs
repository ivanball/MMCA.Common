namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// GDPR/CCPA right-to-erasure fitness functions (ADR-005), both directions:
/// <list type="bullet">
///   <item>Forward: any domain entity that declares a <c>[Pii]</c>-marked property must implement
///   <c>IAnonymizable</c> so it has an erasure path.</item>
///   <item>Reverse: whatever <c>Anonymize()</c> actually overwrites must carry <c>[Pii]</c>, checked
///   behaviorally over <see cref="AnonymizableSamples"/>, plus a coverage fact that every
///   <c>IAnonymizable</c> entity in the module Domain assemblies has a sample.</item>
/// </list>
/// </summary>
/// <remarks>
/// The coverage fact scans the MODULE Domain assemblies only (<see cref="IArchitectureMap.ModuleDomain"/>):
/// the framework's own anonymizable types are checked once, in MMCA.Common's own subclass, rather than
/// re-sampled in every consumer. A repo with no <c>IAnonymizable</c> module entity passes both reverse
/// facts with the default empty table.
/// </remarks>
public abstract class PiiConventionTestsBase
{
    private static readonly IReadOnlyDictionary<Type, Func<object>> NoSamples = new Dictionary<Type, Func<object>>();

    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// Gets one sample per <c>IAnonymizable</c> entity: the entity type mapped to a factory that builds
    /// it through its own domain factory, populated so that every erasable field starts at a value the
    /// erasure has to move. Defaults to empty. Typed <see cref="object"/> because this package takes no
    /// framework reference; the factory must return an instance of its key type.
    /// </summary>
    protected virtual IReadOnlyDictionary<Type, Func<object>> AnonymizableSamples => NoSamples;

    [Fact]
    public void EntitiesWithPiiProperties_ShouldImplement_IAnonymizable() => ArchitectureRules.EntitiesWithPiiImplementAnonymizable(Map);

    [Fact]
    public void EveryAnonymizableDomainEntity_IsCoveredBySample() =>
        ArchitectureRules.AnonymizableEntitiesAreCoveredBySamples(
            Map.ModuleDomain().SelectMany(a => a.LoadableTypes),
            AnonymizableSamples);

    [Fact]
    public void PropertiesOverwrittenByAnonymize_MustCarryPii() =>
        ArchitectureRules.PropertiesOverwrittenByAnonymizeCarryPii(AnonymizableSamples);
}

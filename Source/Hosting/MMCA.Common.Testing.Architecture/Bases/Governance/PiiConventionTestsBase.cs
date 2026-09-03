namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// GDPR/CCPA right-to-erasure fitness function (ADR-005): any domain entity that declares a
/// <c>[Pii]</c>-marked property must implement <c>IAnonymizable</c> so it has an erasure path.
/// </summary>
public abstract class PiiConventionTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    [Fact]
    public void EntitiesWithPiiProperties_ShouldImplement_IAnonymizable() => ArchitectureRules.EntitiesWithPiiImplementAnonymizable(Map);
}

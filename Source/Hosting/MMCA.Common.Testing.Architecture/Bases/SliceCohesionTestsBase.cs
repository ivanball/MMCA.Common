namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Vertical-slice cohesion fitness functions (rubric §5): a use-case slice keeps its command/query,
/// its handler, and its validator together in one namespace, so a feature is a cohesive unit rather
/// than spread across horizontal <c>Handlers/</c>/<c>Validators/</c> folders. Authored once here and
/// re-run as a thin subclass in each repo (MMCA.Common scopes to its Notifications slices; ADC/Store
/// scope to their module Application layers).
/// </summary>
public abstract class SliceCohesionTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    [Fact]
    public void Handlers_ShouldBeCoLocatedWith_TheirContracts() =>
        ArchitectureRules.HandlersAreCoLocatedWithTheirContracts(Map);

    [Fact]
    public void Validators_ShouldBeCoLocatedWith_TheirContracts() =>
        ArchitectureRules.ValidatorsAreCoLocatedWithTheirContracts(Map);
}

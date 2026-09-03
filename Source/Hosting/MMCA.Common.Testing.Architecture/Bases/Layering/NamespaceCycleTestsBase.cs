namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Fitness function: the top-level namespaces inside each layer assembly must form a directed acyclic
/// graph. A namespace cycle is the first visible symptom of a folder pair that has grown into one
/// tangled unit: neither half can be read, tested or lifted into its own service without the other, so
/// the rule guards the framework's extraction promise at the finest granularity the two coarse layer
/// rules (<c>LayerDependencyTestsBase</c>, <c>ModuleIsolationTestsBase</c>) cannot see inside.
/// <para>
/// The rule is signature-level reflection and is blind to method bodies (see
/// <see cref="ArchitectureRules.NamespacesHaveNoDependencyCycles(IArchitectureMap, IReadOnlyCollection{string})"/>),
/// so a green result means "no STRUCTURAL cycle", not "no coupling".
/// </para>
/// </summary>
public abstract class NamespaceCycleTestsBase
{
    /// <summary>The repo's architecture map.</summary>
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// Fully-qualified namespace nodes (e.g. <c>"MMCA.Common.Domain.Entities"</c>) whose cycle is accepted
    /// by design. A cycle is skipped only when EVERY namespace on its reported path appears in this list,
    /// so an allowance can never hide a NEW cycle that merely touches an accepted namespace. Empty by
    /// default; each entry a subclass adds should carry a comment justifying why the tangle is deliberate.
    /// </summary>
    protected virtual IReadOnlyList<string> AllowedCycleNamespaces => [];

    [Fact]
    public void Namespaces_ShouldNotHave_DependencyCycles() =>
        ArchitectureRules.NamespacesHaveNoDependencyCycles(Map, AllowedCycleNamespaces);
}

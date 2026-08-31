using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Architecture.Tests.CascadeFixtures;

/// <summary>
/// Compiled aggregates for <c>CascadeSoftDeleteFitnessTests</c>. The cascade rule reads the IL of a
/// <c>Delete()</c> override, so the only honest way to test it is to compile the cascading and the
/// non-cascading shapes side by side in this assembly and point a map at it.
/// <para>
/// Every fixture is internal and lives in its own namespace, so it is invisible to the framework
/// rules that run over <c>CommonArchitectureMap</c> (the <c>Source/</c> assemblies, never this test
/// assembly) and to the other self-tests, which each point their own fixture map at their own
/// namespace or at type shapes these fixtures do not have.
/// </para>
/// </summary>
internal sealed class CascadeChildFixture : AuditableBaseEntity<int>
{
    public string Label { get; init; } = string.Empty;
}

/// <summary>Cascades through the framework helper. The rule must stay silent about this type.</summary>
internal sealed class HelperCascadingFixture : AuditableAggregateRootEntity<int>
{
    private readonly List<CascadeChildFixture> _children = [];

    public IReadOnlyCollection<CascadeChildFixture> Children => _children;

    public override Result Delete() =>
        Result.Combine(DeleteChildren<CascadeChildFixture, int>(_children), base.Delete());
}

/// <summary>
/// Cascades with a hand-rolled loop over the children, which is what every aggregate did before the
/// helper existed. The rule must accept it too.
/// </summary>
internal sealed class LoopCascadingFixture : AuditableAggregateRootEntity<int>
{
    private readonly List<CascadeChildFixture> _items = [];

    public IReadOnlyCollection<CascadeChildFixture> Items => _items;

    public override Result Delete()
    {
        foreach (var item in _items)
        {
            _ = item.Delete();
        }

        return base.Delete();
    }
}

/// <summary>
/// Owns children and never overrides <c>Delete()</c>, so deleting the root leaves every child row
/// active. The rule must report it, naming the collection.
/// </summary>
internal sealed class MissingOverrideFixture : AuditableAggregateRootEntity<int>
{
    private readonly List<CascadeChildFixture> _orphans = [];

    public IReadOnlyCollection<CascadeChildFixture> Orphans => _orphans;
}

/// <summary>
/// Overrides <c>Delete()</c>, even touches the collection, and still leaves every child row active:
/// detaching the children from the in-memory list is not a soft-delete. This is the shape a naive
/// "does it override Delete" check would pass, which is why the rule reads the body.
/// </summary>
internal sealed class SelfOnlyDeleteFixture : AuditableAggregateRootEntity<int>
{
    private readonly List<CascadeChildFixture> _ignored = [];

    public IReadOnlyCollection<CascadeChildFixture> Ignored => _ignored;

    public override Result Delete()
    {
        _ignored.Clear();

        return base.Delete();
    }
}

/// <summary>
/// The same shape as <see cref="MissingOverrideFixture"/>, kept separate so one self-test can
/// allowlist it and prove an exemption silences exactly the type it names.
/// </summary>
internal sealed class ExemptedOffenderFixture : AuditableAggregateRootEntity<int>
{
    private readonly List<CascadeChildFixture> _exempt = [];

    public IReadOnlyCollection<CascadeChildFixture> Exempt => _exempt;
}

/// <summary>
/// An aggregate with no child ENTITY collection: a list of strings is not an aggregate's children,
/// so the rule must ignore this type even though it never overrides <c>Delete()</c>.
/// </summary>
internal sealed class ChildlessFixture : AuditableAggregateRootEntity<int>
{
    private readonly List<string> _tags = [];

    public IReadOnlyCollection<string> Tags => _tags;
}

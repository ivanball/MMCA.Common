namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Cascade-soft-delete fitness function: an aggregate root that owns child entities must delete them
/// in its own <c>Delete()</c> override. A soft delete is an ordinary UPDATE, so nothing cascades for
/// free: the root vanishes behind the global query filter while its child rows stay ACTIVE, orphaned
/// and unreachable through the root, yet still present for exports, reports and erasure requests.
/// The framework's <c>DeleteChildren&lt;TChild, TChildId&gt;(...)</c> helper is the one-line fix; a
/// hand-rolled loop over the children counts too.
/// <para>
/// Adoption in a repo with existing aggregates: subclass, run once, and move each reported type into
/// a fix (add or extend the <c>Delete()</c> override) or into
/// <see cref="AllowedCascadeExemptTypes"/> with a comment saying why the children are meant to
/// outlive the root. The exemption list is the point of the rule: it turns "delete cascades, mostly"
/// into a reviewed inventory of every aggregate that leaves children behind on purpose.
/// </para>
/// </summary>
public abstract class CascadeSoftDeleteConventionTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// Type full names (<c>MMCA.X.Catalog.Domain.Basket</c>) or namespace prefixes
    /// (<c>MMCA.X.Catalog.Domain.Legacy</c>) whose child entities deliberately survive the root's
    /// soft-delete. Empty by default, which requires every child-bearing aggregate to cascade.
    /// </summary>
    protected virtual IReadOnlyCollection<string> AllowedCascadeExemptTypes => [];

    [Fact]
    public void AggregatesWithChildCollections_MustCascadeSoftDelete_InDelete() =>
        ArchitectureRules.AggregatesCascadeSoftDeleteToChildren(Map, AllowedCascadeExemptTypes);
}

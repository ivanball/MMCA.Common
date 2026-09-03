namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Soft-delete enforcement fitness function: EF Core's row-erasing members
/// (<c>DbSet.Remove</c>/<c>RemoveRange</c>, <c>DbContext.Remove</c>/<c>RemoveRange</c>,
/// <c>ExecuteDelete</c>/<c>ExecuteDeleteAsync</c>) may only be called from the purge and erasure
/// types the repo names in <see cref="AllowedHardDeleteTypes"/>. Everything else deletes by setting
/// <c>IsDeleted = true</c>, which is what keeps a deleted row auditable, restorable and accounted
/// for under an erasure request.
/// <para>
/// Adoption in a repo with existing hard deletes: subclass, run once, and move each reported type
/// into <see cref="AllowedHardDeleteTypes"/> with a comment saying why erasing is correct there.
/// The list is the point of the rule: it turns "we soft-delete, mostly" into a reviewed inventory
/// of every place that does not.
/// </para>
/// </summary>
public abstract class SoftDeleteEnforcementTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// Type full names (<c>MMCA.X.Infrastructure.Purge.RetentionSweeper</c>) or namespace prefixes
    /// (<c>MMCA.X.Infrastructure.Purge</c>) where erasing a row is the deliberate requirement:
    /// retention/cleanup jobs, outbox and audit-trail sweepers, GDPR erasure handlers (ADR-005).
    /// Empty by default, which bans hard deletes outright.
    /// </summary>
    protected virtual IReadOnlyCollection<string> AllowedHardDeleteTypes => [];

    [Fact]
    public void HardDeletes_ShouldOnlyOccurIn_AllowedPurgeTypes() =>
        ArchitectureRules.HardDeletesOnlyInAllowedTypes(Map, AllowedHardDeleteTypes);
}

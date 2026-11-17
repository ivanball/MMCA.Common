namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Soft-delete enforcement fitness function: EF Core's row-erasing members
/// (<c>DbSet.Remove</c>/<c>RemoveRange</c>, <c>DbContext.Remove</c>/<c>RemoveRange</c>,
/// <c>ExecuteDelete</c>/<c>ExecuteDeleteAsync</c>) may only be called from MMCA.Common's own reviewed
/// erasure types (always allowed by this base) and the purge and erasure types the repo names in
/// <see cref="AllowedHardDeleteTypes"/>. Everything else deletes by setting <c>IsDeleted = true</c>,
/// which is what keeps a deleted row auditable, restorable and accounted for under an erasure request.
/// <para>
/// Adoption in a repo with existing hard deletes: subclass, run once, and move each reported type
/// into <see cref="AllowedHardDeleteTypes"/> with a comment saying why erasing is correct there.
/// The list is the point of the rule: it turns "we soft-delete, mostly" into a reviewed inventory
/// of every place that does not.
/// </para>
/// </summary>
public abstract class SoftDeleteEnforcementTestsBase
{
    /// <summary>
    /// The MMCA.Common.Infrastructure types that erase rows by design, allowed for every subclass so a
    /// consumer scanning the framework assemblies does not re-list them. Each entry is named
    /// individually rather than exempting a namespace, so a NEW framework eraser fails Common's own
    /// run of this rule and gets reviewed before it ships.
    /// </summary>
    private static readonly string[] FrameworkHardDeleteTypes =
    [
        // The framework's own set-based delete escape hatch: the ONE place the abstraction can be
        // reached from, so the rule catches it at the implementation and leaves IRepository free to
        // be used everywhere else. Erasing is the caller's explicit ask here (the method is named
        // ExecuteDeleteAsync), and the calling convention is that it targets derived rows the caller
        // is about to rewrite, never user data that carries an audit or erasure obligation.
        "MMCA.Common.Infrastructure.Persistence.Repositories.EFRepository`2",

        // The operator-facing internal-command purge: it erases only rows that already completed.
        // Queue rows are execution plumbing with a bounded lifetime whose payload can carry personal
        // data.
        "MMCA.Common.Infrastructure.Persistence.InternalCommands.Administration.InternalCommandAdministration",

        // The one delete loop every retention sweep goes through, and it refuses a soft-deletable
        // entity. For each sweep, erasing IS the retention policy: outbox, inbox and queue rows are
        // plumbing with a bounded lifetime that soft-deleting would grow without bound; keeping an
        // audit row forever is the privacy defect, not the safeguard; and a refresh session has no
        // IsDeleted flag, only a credential digest plus a device's IP and user-agent (ADR-005).
        "MMCA.Common.Infrastructure.Persistence.Retention.BatchedDelete",

        // Stored permission grants. A grant row is a set membership, not an aggregate: revoking a
        // capability means the row is not there, and a soft-deleted grant would have to be filtered
        // out of every authorization read forever, with a missed filter failing OPEN. It carries no
        // audit or erasure obligation either: its whole content is a role name and a permission name.
        "MMCA.Common.Infrastructure.Persistence.Auth.EFPermissionGrantStore",

        // View-as grant retention (ADR-137). A grant row is framework bookkeeping like a refresh
        // session (no IsDeleted, no audit stamps); erasing it past ViewAs:RetentionDays IS the
        // retention requirement, and the sweep is its only delete.
        "MMCA.Common.Infrastructure.Persistence.Auth.EFViewAsGrantStore",

        // Passkeys (ADR-139). A passkey row is framework bookkeeping (no IsDeleted, no audit stamps)
        // whose whole content is a public key. Deleting one must stop it signing in: a soft-deleted
        // credential would have to be filtered out of the sign-in lookup forever, with a missed filter
        // failing OPEN on a key the user removed.
        "MMCA.Common.Infrastructure.Persistence.Auth.EFPasskeyCredentialStore",
    ];

    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// The repo's own type full names (<c>MMCA.X.Infrastructure.Purge.RetentionSweeper</c>) or
    /// namespace prefixes (<c>MMCA.X.Infrastructure.Purge</c>) where erasing a row is the deliberate
    /// requirement: retention/cleanup jobs, outbox and audit-trail sweepers, GDPR erasure handlers
    /// (ADR-005). MMCA.Common's own erasure types are always allowed by the base and need not be
    /// listed here. Empty by default, which bans hard deletes outside the framework outright.
    /// </summary>
    protected virtual IReadOnlyCollection<string> AllowedHardDeleteTypes => [];

    [Fact]
    public void HardDeletes_ShouldOnlyOccurIn_AllowedPurgeTypes() =>
        ArchitectureRules.HardDeletesOnlyInAllowedTypes(
            Map,
            [.. FrameworkHardDeleteTypes, .. AllowedHardDeleteTypes]);
}

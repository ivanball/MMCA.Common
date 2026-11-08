using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Domain;

/// <summary>
/// Soft-delete enforcement guard (ADR-005), driven by the shared
/// <see cref="SoftDeleteEnforcementTestsBase"/>: the framework deletes by setting
/// <c>IsDeleted = true</c>, so EF Core's row-erasing members are banned outside the reviewed types
/// listed in <see cref="AllowedHardDeleteTypes"/>.
/// <para>
/// This is the rule turned back on its own author. Store and ADC subclass the same base and
/// allowlist the framework erasers they run, because no module of theirs erases a row of its own;
/// running it here is what keeps that list honest, since a NEW framework eraser would otherwise land
/// in the packages and fail downstream instead of in this repo. Each entry is named individually
/// rather than exempting a namespace, so another eraser under
/// <c>MMCA.Common.Infrastructure.Persistence</c> still fails here and gets reviewed.
/// </para>
/// </summary>
public sealed class SoftDeleteEnforcementTests : SoftDeleteEnforcementTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();

    /// <inheritdoc />
    protected override IReadOnlyCollection<string> AllowedHardDeleteTypes =>
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
}

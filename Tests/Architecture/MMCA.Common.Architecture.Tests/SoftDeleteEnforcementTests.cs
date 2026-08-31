using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Soft-delete enforcement guard (ADR-005), driven by the shared
/// <see cref="SoftDeleteEnforcementTestsBase"/>: the framework deletes by setting
/// <c>IsDeleted = true</c>, so EF Core's row-erasing members are banned outside the reviewed types
/// listed in <see cref="AllowedHardDeleteTypes"/>.
/// <para>
/// This is the rule turned back on its own author. Store and ADC subclass the same base and
/// allowlist exactly these four framework types, because no module of theirs erases a row of its
/// own; running it here is what keeps that list honest, since a NEW framework eraser would
/// otherwise land in the packages and fail downstream instead of in this repo. Each entry is named
/// individually rather than exempting a namespace, so a fifth eraser under
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

        // Outbox and inbox retention (PurgeAsync, PurgeInboxAsync, SweepDeadLettersAsync). These
        // rows are delivery plumbing with a bounded lifetime, and the sweep IS the retention policy;
        // soft-deleting them would grow the table the job exists to bound.
        "MMCA.Common.Infrastructure.Persistence.Outbox.OutboxCleanupService",

        // Audit-trail retention. Erasing past the retention window IS the requirement (keeping an
        // audit row forever is the privacy defect, not the safeguard).
        "MMCA.Common.Infrastructure.Persistence.AuditTrail.AuditTrailCleanupJob",

        // Refresh-session retention. A session row is framework bookkeeping, not an aggregate: it
        // carries no IsDeleted flag and no audit stamps, and its content is a credential digest plus
        // the IP and user-agent of a device. Flagging it instead of erasing it would keep a growing
        // record of a data subject's devices past any use for it (ADR-005).
        "MMCA.Common.Infrastructure.Persistence.Auth.RefreshSessionCleanupService",
    ];
}

using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// State-management convention rules (§19), driven by the shared
/// <see cref="StateManagementConventionTestsBase"/>: the shared <c>MMCA.Common.UI</c> assembly carries
/// no mutable static state (a static member is shared across every Blazor Server circuit) and its
/// stateful services stay scoped, so the per-circuit state model is CI-enforced.
/// </summary>
public sealed class StateManagementConventionTests : StateManagementConventionTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();

    /// <summary>
    /// <c>ErrorMessages._localizer</c> is a write-once wiring seam, not per-user state: the root layout
    /// configures the shared <c>IStringLocalizer</c> exactly once (idempotent), and the localizer itself
    /// resolves against the ambient UI culture per call, so no user's state leaks to another. Keeping the
    /// static message API is deliberate (every consumer call site depends on it, ADR-027).
    /// </summary>
    protected override IReadOnlyList<string> AllowedStaticMembers =>
        ["MMCA.Common.UI.Pages.Common.ErrorMessages._localizer"];
}

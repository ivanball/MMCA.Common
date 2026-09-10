namespace MMCA.Common.Testing.Aspire.Fixtures;

/// <summary>
/// The two time budgets an AppHost-backed collection spends: building and starting the application,
/// then waiting for the resources to report ready.
/// <para>
/// They are separate because they fail for different reasons. Startup ends when the orchestrator has
/// launched every resource, so it is dominated by container image pulls on a cold agent; readiness
/// ends when the last awaited resource turns healthy, so it is dominated by migrations, warm-up and
/// dependency graphs. Collapsing them into one number makes a slow pull and a wedged health check
/// indistinguishable in the failure message.
/// </para>
/// </summary>
/// <param name="Startup">Budget for building and starting the application.</param>
/// <param name="Readiness">Budget for every awaited resource to report ready, shared across them.</param>
public sealed record AppHostReadinessBudget(TimeSpan Startup, TimeSpan Readiness)
{
    /// <summary>
    /// Five minutes each. Generous on purpose: the first run on a cold agent pulls container images
    /// before a single process starts, and a budget tight enough to be "fast" only converts a slow
    /// agent into a red build.
    /// </summary>
    public static AppHostReadinessBudget Default { get; } =
        new(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

    /// <summary>
    /// What is left of the readiness budget after <paramref name="elapsed"/>, floored at zero.
    /// <para>
    /// The readiness budget is a single deadline shared by every awaited resource, not a per-resource
    /// allowance: with five resources and a per-resource budget, a stack that wedges on the last one
    /// takes five times as long to say so. Subtracting elapsed time before each wait keeps the total
    /// bounded and lets the fixture name the resource the budget ran out on.
    /// </para>
    /// </summary>
    /// <param name="elapsed">Time already spent waiting for readiness.</param>
    /// <returns>The remaining budget, never negative.</returns>
    public TimeSpan Remaining(TimeSpan elapsed) =>
        elapsed >= Readiness ? TimeSpan.Zero : Readiness - elapsed;

    /// <summary>
    /// Rejects a budget that cannot succeed. A non-positive budget would cancel its own first wait,
    /// which surfaces as an unexplained timeout rather than as the configuration mistake it is.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Either budget is zero or negative.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Startup, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Readiness, TimeSpan.Zero);
    }
}

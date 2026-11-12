namespace MMCA.Common.Testing.Aspire.Outages;

/// <summary>
/// The three time budgets an outage scenario spends: a resource changing state after a stop or start
/// command, a probe reaching its expected answer, and the outbox draining after recovery.
/// </summary>
/// <param name="StateChange">Budget for a resource to report stopped after a stop, or healthy after a start.</param>
/// <param name="Probe">Budget for a probe to reach its expected status code.</param>
/// <param name="Drain">Budget for the outbox to drain after the dependency is back.</param>
public sealed record DependencyOutageBudget(TimeSpan StateChange, TimeSpan Probe, TimeSpan Drain)
{
    /// <summary>
    /// Two minutes for a state change, ninety seconds for a probe, three minutes for a drain.
    /// </summary>
    public static DependencyOutageBudget Default { get; } =
        new(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(90), TimeSpan.FromMinutes(3));

    /// <summary>
    /// Rejects a budget that cannot succeed. A non-positive budget would cancel its own first wait,
    /// which surfaces as an unexplained timeout rather than as the configuration mistake it is.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Any budget is zero or negative.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(StateChange, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Probe, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Drain, TimeSpan.Zero);
    }
}

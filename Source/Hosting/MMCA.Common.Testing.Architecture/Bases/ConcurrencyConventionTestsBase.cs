namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Optimistic-concurrency fitness function: every <c>*UpdateRequest</c> implements <c>IConcurrencyAware</c>
/// (carries a RowVersion), so concurrent edits surface as 409 Conflict rather than silent last-write-wins.
/// Modules with no mutable aggregate are legitimately vacuous.
/// </summary>
public abstract class ConcurrencyConventionTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    [Fact]
    public void UpdateRequests_ShouldImplement_IConcurrencyAware() => ArchitectureRules.UpdateRequestsAreConcurrencyAware(Map);
}

namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Optimistic-concurrency fitness function: no <c>*UpdateRequest</c> implements
/// <c>IConcurrencyAware</c>. The token a conditional update is checked against is read from the
/// <c>If-Match</c> request header, so a token in the request body would give the same check a
/// second, competing source. Modules with no mutable aggregate are legitimately vacuous.
/// </summary>
public abstract class ConcurrencyConventionTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    [Fact]
    public void UpdateRequests_ShouldNotImplement_IConcurrencyAware() => ArchitectureRules.UpdateRequestsAreNotConcurrencyAware(Map);
}

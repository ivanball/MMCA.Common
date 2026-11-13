using AwesomeAssertions;
using MMCA.Common.Testing.Aspire.Outages;

namespace MMCA.Common.Testing.Aspire.Tests.Outages;

/// <summary>
/// The three budgets an outage scenario spends: a resource changing state, a probe reaching its
/// expected answer, and the outbox draining after recovery. A non-positive budget would cancel its
/// own first wait, so it must fail as the configuration mistake it is.
/// </summary>
public sealed class DependencyOutageBudgetTests
{
    [Fact]
    public void Default_IsTwoMinutes_NinetySeconds_ThreeMinutes()
    {
        var budget = DependencyOutageBudget.Default;

        budget.StateChange.Should().Be(TimeSpan.FromMinutes(2));
        budget.Probe.Should().Be(TimeSpan.FromSeconds(90));
        budget.Drain.Should().Be(TimeSpan.FromMinutes(3));
    }

    [Fact]
    public void Validate_AcceptsTheDefault()
    {
        var act = DependencyOutageBudget.Default.Validate;

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0, 60, 60)]
    [InlineData(-1, 60, 60)]
    [InlineData(60, 0, 60)]
    [InlineData(60, -1, 60)]
    [InlineData(60, 60, 0)]
    [InlineData(60, 60, -1)]
    public void Validate_RejectsZeroOrNegative(int stateChangeSeconds, int probeSeconds, int drainSeconds)
    {
        var budget = new DependencyOutageBudget(
            TimeSpan.FromSeconds(stateChangeSeconds),
            TimeSpan.FromSeconds(probeSeconds),
            TimeSpan.FromSeconds(drainSeconds));

        var act = budget.Validate;

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

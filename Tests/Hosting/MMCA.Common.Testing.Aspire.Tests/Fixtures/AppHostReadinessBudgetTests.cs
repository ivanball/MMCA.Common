using AwesomeAssertions;
using MMCA.Common.Testing.Aspire.Fixtures;

namespace MMCA.Common.Testing.Aspire.Tests.Fixtures;

/// <summary>
/// The readiness budget is a single deadline shared by every awaited resource, so the subtraction is
/// what bounds a wedged stack. Getting it wrong by letting it go negative would hand a
/// <see cref="CancellationTokenSource"/> a negative delay and throw somewhere unrelated.
/// </summary>
public sealed class AppHostReadinessBudgetTests
{
    private static readonly AppHostReadinessBudget TwoMinutes =
        new(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));

    [Fact]
    public void Default_IsGenerous() =>
        AppHostReadinessBudget.Default.Startup
            .Should().BeGreaterThanOrEqualTo(
                TimeSpan.FromMinutes(5),
                "a cold agent pulls container images before a single process starts");

    [Fact]
    public void Remaining_SubtractsElapsed() =>
        TwoMinutes.Remaining(TimeSpan.FromSeconds(30)).Should().Be(TimeSpan.FromSeconds(90));

    [Fact]
    public void Remaining_IsZeroAtTheDeadline() =>
        TwoMinutes.Remaining(TimeSpan.FromMinutes(2)).Should().Be(TimeSpan.Zero);

    [Fact]
    public void Remaining_NeverGoesNegative() =>
        TwoMinutes.Remaining(TimeSpan.FromHours(1))
            .Should().Be(TimeSpan.Zero, "a negative delay is a throw, not a timeout");

    [Fact]
    public void Remaining_IsTheWholeBudgetBeforeAnythingIsSpent() =>
        TwoMinutes.Remaining(TimeSpan.Zero).Should().Be(TimeSpan.FromMinutes(2));

    [Fact]
    public void Validate_AcceptsPositiveBudgets()
    {
        var act = TwoMinutes.Validate;

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(60, 0)]
    [InlineData(-1, 60)]
    [InlineData(60, -1)]
    public void Validate_RejectsANonPositiveBudget(int startupSeconds, int readinessSeconds)
    {
        var budget = new AppHostReadinessBudget(
            TimeSpan.FromSeconds(startupSeconds),
            TimeSpan.FromSeconds(readinessSeconds));

        var act = budget.Validate;

        act.Should().Throw<ArgumentOutOfRangeException>(
            "a budget that cancels its own first wait must fail as the configuration mistake it is");
    }
}

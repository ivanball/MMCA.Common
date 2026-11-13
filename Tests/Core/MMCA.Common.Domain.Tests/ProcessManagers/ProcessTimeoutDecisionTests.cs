using AwesomeAssertions;
using MMCA.Common.Domain.ProcessManagers;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Domain.Tests.ProcessManagers;

/// <summary>
/// Tests for <see cref="ProcessTimeoutDecision"/>: the four outcomes an aggregate returns from
/// <c>DecideTimeout</c>. Only NotYetDue carries a due instant and only Rejected carries errors, and a
/// rejection without a reason is refused.
/// </summary>
public sealed class ProcessTimeoutDecisionTests
{
    [Fact]
    public void Rejected_WithNoErrors_Throws()
    {
        var act = () => ProcessTimeoutDecision.Rejected([]);

        act.Should().Throw<ArgumentException>("a rejection must say why the stored state refused the timeout");
    }

    [Fact]
    public void Rejected_CarriesTheErrors()
    {
        IReadOnlyList<Error> errors = [Error.Invariant("Widgets.CannotExpire", "The widget cannot expire.")];

        var decision = ProcessTimeoutDecision.Rejected(errors);

        decision.Outcome.Should().Be(ProcessTimeoutOutcome.Rejected);
        decision.Errors.Should().Equal(errors);
        decision.DueAt.Should().BeNull();
    }

    [Fact]
    public void NotYetDue_CarriesDueAt()
    {
        var dueAt = new DateTime(2026, 11, 12, 10, 30, 0, DateTimeKind.Utc);

        var decision = ProcessTimeoutDecision.NotYetDue(dueAt);

        decision.Outcome.Should().Be(ProcessTimeoutOutcome.NotYetDue);
        decision.DueAt.Should().Be(dueAt);
        decision.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Applied_HasNoErrors()
    {
        var decision = ProcessTimeoutDecision.Applied();

        decision.Outcome.Should().Be(ProcessTimeoutOutcome.Applied);
        decision.Errors.Should().BeEmpty();
        decision.DueAt.Should().BeNull();
    }

    [Fact]
    public void NotApplicable_HasNoDueAtAndNoErrors()
    {
        var decision = ProcessTimeoutDecision.NotApplicable();

        decision.Outcome.Should().Be(ProcessTimeoutOutcome.NotApplicable);
        decision.Errors.Should().BeEmpty();
        decision.DueAt.Should().BeNull();
    }

    [Fact]
    public void Outcome_ValuesArePinned()
    {
        ((int)ProcessTimeoutOutcome.NotApplicable).Should().Be(0);
        ((int)ProcessTimeoutOutcome.NotYetDue).Should().Be(1);
        ((int)ProcessTimeoutOutcome.Applied).Should().Be(2);
        ((int)ProcessTimeoutOutcome.Rejected).Should().Be(3);
    }
}

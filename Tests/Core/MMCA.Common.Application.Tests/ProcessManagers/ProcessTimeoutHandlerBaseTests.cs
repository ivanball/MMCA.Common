using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.ProcessManagers;
using MMCA.Common.Domain.ProcessManagers;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.ProcessManagers;

/// <summary>
/// Tests for <see cref="ProcessTimeoutHandlerBase{TCommand, TProcess, TProcessId}"/> (design item 36,
/// rules 6 to 10): the base loads the process tracked with the subclass's includes, captures the state
/// description BEFORE the aggregate decides, saves exactly once and only when the aggregate applied the
/// timeout, treats a lost concurrency race as handled, and lets every other save failure propagate so
/// the internal command row retries. Every outcome reaches <c>OnHandled</c> as a report.
/// </summary>
public sealed class ProcessTimeoutHandlerBaseTests
{
    private const int ProcessId = 7;
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 30, 0, TimeSpan.Zero);

    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IRepository<TimeoutTestProcess, int>> _repository = new();
    private readonly Mock<IConcurrencyConflictDetector> _concurrencyConflictDetector = new();
    private readonly FakeTimeProvider _timeProvider = new(Now);

    public ProcessTimeoutHandlerBaseTests() =>
        _unitOfWork.Setup(u => u.GetRepository<TimeoutTestProcess, int>()).Returns(_repository.Object);

    [Fact]
    public async Task NullCommand_Throws()
    {
        var act = () => CreateHandler().HandleAsync(null!, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task LoadsTrackedWithIncludes()
    {
        ReturnsProcess(new TimeoutTestProcess { Id = ProcessId });
        using var cts = new CancellationTokenSource();

        await CreateHandler().HandleAsync(new TestTimeoutCommand(ProcessId), cts.Token);

        _repository.Verify(
            r => r.GetByIdAsync(
                ProcessId,
                It.Is<IEnumerable<string>>(includes => includes.SequenceEqual(new[] { TestTimeoutHandler.LinesInclude })),
                true,
                cts.Token),
            Times.Once,
            "the aggregate decides and may mutate, so it is loaded tracked with the subclass's includes");
    }

    [Fact]
    public async Task Missing_SucceedsReportsProcessMissingNoSave()
    {
        _repository
            .Setup(r => r.GetByIdAsync(ProcessId, It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TimeoutTestProcess?)null);
        var handler = CreateHandler();

        var result = await handler.HandleAsync(new TestTimeoutCommand(ProcessId), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue("a process that no longer exists has nothing left to time out, so the row completes");
        var report = handler.Reports.Should().ContainSingle().Which;
        report.Handling.Should().Be(ProcessTimeoutHandling.ProcessMissing);
        report.ProcessId.Should().Be(ProcessId);
        report.DueAt.Should().BeNull();
        report.Errors.Should().BeEmpty();
        report.StateBefore.Should().BeNull("there is no process to describe");
        VerifyNoSave();
    }

    [Fact]
    public async Task NotApplicable_NoSave()
    {
        var process = new TimeoutTestProcess { Id = ProcessId, NextDecision = ProcessTimeoutDecision.NotApplicable() };
        ReturnsProcess(process);
        var handler = CreateHandler();

        var result = await handler.HandleAsync(new TestTimeoutCommand(ProcessId), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        var report = handler.Reports.Should().ContainSingle().Which;
        report.Handling.Should().Be(ProcessTimeoutHandling.NotApplicable);
        report.ProcessId.Should().Be(ProcessId);
        report.TimeoutName.Should().Be(TestTimeoutHandler.TimeoutName);
        report.DueAt.Should().BeNull();
        report.Errors.Should().BeEmpty();
        report.StateBefore.Should().Be(TimeoutTestProcess.PendingStatus);
        VerifyNoSave();
    }

    [Fact]
    public async Task NotYetDue_ReportsDueAtNoSave()
    {
        var dueAt = Now.UtcDateTime.AddMinutes(10);
        var process = new TimeoutTestProcess { Id = ProcessId, NextDecision = ProcessTimeoutDecision.NotYetDue(dueAt) };
        ReturnsProcess(process);
        var handler = CreateHandler();

        var result = await handler.HandleAsync(new TestTimeoutCommand(ProcessId), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue("an early row is a stale or re-armed timeout; the live row will fire later");
        var report = handler.Reports.Should().ContainSingle().Which;
        report.Handling.Should().Be(ProcessTimeoutHandling.NotYetDue);
        report.DueAt.Should().Be(dueAt);
        report.Errors.Should().BeEmpty();
        report.StateBefore.Should().Be(TimeoutTestProcess.PendingStatus);
        VerifyNoSave();
    }

    [Fact]
    public async Task Rejected_SucceedsReportsErrorsNoSave()
    {
        IReadOnlyList<Error> errors = [Error.Invariant("Widgets.CannotExpire", "The widget cannot expire.")];
        var process = new TimeoutTestProcess { Id = ProcessId, NextDecision = ProcessTimeoutDecision.Rejected(errors) };
        ReturnsProcess(process);
        var handler = CreateHandler();

        var result = await handler.HandleAsync(new TestTimeoutCommand(ProcessId), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue("the stored state decided the rejection, so retrying the row cannot change it");
        var report = handler.Reports.Should().ContainSingle().Which;
        report.Handling.Should().Be(ProcessTimeoutHandling.Rejected);
        report.Errors.Should().Equal(errors);
        report.DueAt.Should().BeNull();
        report.StateBefore.Should().Be(TimeoutTestProcess.PendingStatus);
        VerifyNoSave();
    }

    [Fact]
    public async Task Applied_SavesOnceReportsStateBefore()
    {
        var process = new TimeoutTestProcess { Id = ProcessId, NextDecision = ProcessTimeoutDecision.Applied() };
        ReturnsProcess(process);
        var handler = CreateHandler();
        using var cts = new CancellationTokenSource();

        var result = await handler.HandleAsync(new TestTimeoutCommand(ProcessId), cts.Token);

        result.IsSuccess.Should().BeTrue();
        _unitOfWork.Verify(u => u.SaveChangesAsync(cts.Token), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        process.Status.Should().Be(TimeoutTestProcess.ExpiredStatus, "the test aggregate transitions when it applies the timeout");
        var report = handler.Reports.Should().ContainSingle().Which;
        report.Handling.Should().Be(ProcessTimeoutHandling.Applied);
        report.ProcessId.Should().Be(ProcessId);
        report.TimeoutName.Should().Be(TestTimeoutHandler.TimeoutName);
        report.Errors.Should().BeEmpty();
        report.StateBefore.Should().Be(
            TimeoutTestProcess.PendingStatus,
            "DescribeState is captured BEFORE the aggregate decides, so the report names the state the timeout left");
    }

    [Fact]
    public async Task ConcurrencyConflict_SucceedsReportsLostRace()
    {
        var conflict = new InvalidOperationException("Row version mismatch.");
        ReturnsProcess(new TimeoutTestProcess { Id = ProcessId, NextDecision = ProcessTimeoutDecision.Applied() });
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(conflict);
        _concurrencyConflictDetector.Setup(d => d.IsConcurrencyConflict(conflict)).Returns(true);
        var handler = CreateHandler();

        var result = await handler.HandleAsync(new TestTimeoutCommand(ProcessId), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue("another writer moved the process first; its transition already decided the outcome");
        _concurrencyConflictDetector.Verify(d => d.IsConcurrencyConflict(conflict), Times.Once);
        var report = handler.Reports.Should().ContainSingle().Which;
        report.Handling.Should().Be(ProcessTimeoutHandling.LostRace);
        report.ProcessId.Should().Be(ProcessId);
        report.StateBefore.Should().Be(TimeoutTestProcess.PendingStatus);
    }

    [Fact]
    public async Task OtherSaveException_Propagates()
    {
        var failure = new InvalidOperationException("The database is unreachable.");
        ReturnsProcess(new TimeoutTestProcess { Id = ProcessId, NextDecision = ProcessTimeoutDecision.Applied() });
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        _concurrencyConflictDetector.Setup(d => d.IsConcurrencyConflict(It.IsAny<Exception>())).Returns(false);
        var handler = CreateHandler();

        var act = () => handler.HandleAsync(new TestTimeoutCommand(ProcessId), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>("the row must retry")).Which.Should().BeSameAs(failure);
        handler.Reports.Should().NotContain(
            r => r.Handling == ProcessTimeoutHandling.Applied || r.Handling == ProcessTimeoutHandling.LostRace,
            "nothing was applied and no race was lost");
    }

    [Fact]
    public async Task PassesUtcNowAndResolvedTimeoutToAggregate()
    {
        var process = new TimeoutTestProcess { Id = ProcessId, NextDecision = ProcessTimeoutDecision.NotApplicable() };
        ReturnsProcess(process);
        var handler = CreateHandler();
        var command = new TestTimeoutCommand(ProcessId);

        await handler.HandleAsync(command, TestContext.Current.CancellationToken);

        process.DecideCalls.Should().Be(1);
        process.ReceivedTimeout.Should().BeSameAs(handler.ResolvedTimeout, "the aggregate decides against the subclass's resolved timeout");
        process.ReceivedUtcNow.Should().Be(Now.UtcDateTime, "the instant comes from the injected TimeProvider");
        process.ReceivedUtcNow!.Value.Kind.Should().Be(DateTimeKind.Utc);
        handler.ResolvedFor.Should().ContainSingle().Which.Should().BeSameAs(command);
    }

    [Fact]
    public void Handling_ValuesArePinned()
    {
        ((int)ProcessTimeoutHandling.ProcessMissing).Should().Be(0);
        ((int)ProcessTimeoutHandling.NotApplicable).Should().Be(1);
        ((int)ProcessTimeoutHandling.NotYetDue).Should().Be(2);
        ((int)ProcessTimeoutHandling.Applied).Should().Be(3);
        ((int)ProcessTimeoutHandling.Rejected).Should().Be(4);
        ((int)ProcessTimeoutHandling.LostRace).Should().Be(5);
    }

    private TestTimeoutHandler CreateHandler() =>
        new(_unitOfWork.Object, _concurrencyConflictDetector.Object, _timeProvider);

    private void ReturnsProcess(TimeoutTestProcess process) =>
        _repository
            .Setup(r => r.GetByIdAsync(ProcessId, It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(process);

    private void VerifyNoSave() =>
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
}

// -- Test doubles (public: Moq proxies IRepository<TimeoutTestProcess, int>) --

/// <summary>A consumer-owned timeout command; ProcessId implemented implicitly here.</summary>
public sealed record TestTimeoutCommand(int ProcessId) : IProcessTimeoutCommand<int>;

/// <summary>
/// A process aggregate whose timeout decision is configured by the test. It records what it was asked
/// and moves from Pending to Expired when it applies the timeout, so a state description taken after
/// the decision would differ from one taken before it.
/// </summary>
public sealed class TimeoutTestProcess : ProcessManagerAggregateRoot<int>
{
    public const string PendingStatus = "Pending";
    public const string ExpiredStatus = "Expired";

    public string Status { get; private set; } = PendingStatus;

    public ProcessTimeoutDecision NextDecision { get; init; } = ProcessTimeoutDecision.NotApplicable();

    public int DecideCalls { get; private set; }

    public ProcessTimeout? ReceivedTimeout { get; private set; }

    public DateTime? ReceivedUtcNow { get; private set; }

    public override ProcessTimeoutDecision DecideTimeout(ProcessTimeout timeout, DateTime utcNow)
    {
        DecideCalls++;
        ReceivedTimeout = timeout;
        ReceivedUtcNow = utcNow;

        if (NextDecision.Outcome == ProcessTimeoutOutcome.Applied)
        {
            Status = ExpiredStatus;
        }

        return NextDecision;
    }
}

/// <summary>A derived timeout handler that observes the base through its declared protected hooks only.</summary>
public sealed class TestTimeoutHandler(
    IUnitOfWork unitOfWork,
    IConcurrencyConflictDetector concurrencyConflictDetector,
    TimeProvider timeProvider)
    : ProcessTimeoutHandlerBase<TestTimeoutCommand, TimeoutTestProcess, int>(unitOfWork, concurrencyConflictDetector, timeProvider)
{
    public const string LinesInclude = "Lines";
    public const string TimeoutName = "WidgetExpiry";

    public ProcessTimeout ResolvedTimeout { get; } = new(TimeoutName, TimeSpan.FromMinutes(30));

    public List<TestTimeoutCommand> ResolvedFor { get; } = [];

    public List<ProcessTimeoutReport<int>> Reports { get; } = [];

    protected override IEnumerable<string> Includes => [LinesInclude];

    protected override ProcessTimeout ResolveTimeout(TestTimeoutCommand command)
    {
        ResolvedFor.Add(command);
        return ResolvedTimeout;
    }

    protected override string? DescribeState(TimeoutTestProcess process) => process.Status;

    protected override void OnHandled(TestTimeoutCommand command, ProcessTimeoutReport<int> report) => Reports.Add(report);
}

using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.InternalCommands;
using MMCA.Common.Application.ProcessManagers;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.ProcessManagers;

/// <summary>
/// Tests for <see cref="ProcessTimeoutScheduler"/> (design item 36, rules 3 to 5): arming a timeout is
/// one internal command scheduled through the runAt overload at now plus the delay, the internal
/// command scheduler's result is returned unchanged, and a delay that is not in the future fails
/// validation and schedules nothing.
/// </summary>
public sealed class ProcessTimeoutSchedulerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 30, 0, TimeSpan.Zero);

    private readonly Mock<IInternalCommandScheduler> _internalCommandScheduler = new();
    private readonly FakeTimeProvider _timeProvider = new(Now);

    [Fact]
    public async Task UsesRunAtOverloadAtNowPlusAfter()
    {
        var command = new ExpireWidgetTimeout(7);
        var scheduled = Result.Success(Guid.NewGuid());
        using var cts = new CancellationTokenSource();
        _internalCommandScheduler
            .Setup(s => s.ScheduleAsync(It.IsAny<IInternalCommand>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(scheduled);

        var result = await CreateScheduler().ArmAsync(command, TimeSpan.FromMinutes(15), cts.Token);

        result.Should().BeSameAs(scheduled, "the internal command scheduler's result is returned unchanged");
        _internalCommandScheduler.Verify(
            s => s.ScheduleAsync(
                It.Is<IInternalCommand>(c => ReferenceEquals(c, command)),
                (DateTimeOffset?)Now.AddMinutes(15),
                cts.Token),
            Times.Once);
        _internalCommandScheduler.Verify(
            s => s.ScheduleAsync(It.IsAny<IInternalCommand>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the runAt overload fixes the due instant from the injected clock; the delay overload would read the scheduler's own clock");
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-TimeSpan.TicksPerSecond)]
    public async Task ZeroAfter_FailsAndSchedulesNothing(long afterTicks)
    {
        var result = await CreateScheduler().ArmAsync(
            new ExpireWidgetTimeout(7),
            TimeSpan.FromTicks(afterTicks),
            TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("ProcessManagers.TimeoutNotInFuture");
        error.Type.Should().Be(ErrorType.Validation);
        _internalCommandScheduler.Verify(
            s => s.ScheduleAsync(It.IsAny<IInternalCommand>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _internalCommandScheduler.Verify(
            s => s.ScheduleAsync(It.IsAny<IInternalCommand>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SchedulerFailure_IsReturned()
    {
        var failure = Result.Failure<Guid>(Error.Failure("InternalCommands.Unavailable", "The queue is unavailable."));
        _internalCommandScheduler
            .Setup(s => s.ScheduleAsync(It.IsAny<IInternalCommand>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(failure);

        var result = await CreateScheduler().ArmAsync(
            new ExpireWidgetTimeout(7),
            TimeSpan.FromMinutes(15),
            TestContext.Current.CancellationToken);

        result.Should().BeSameAs(failure, "a scheduling failure is the caller's to handle, not the primitive's to rewrite");
    }

    [Fact]
    public void ExplicitProcessId_IsNotPartOfTheStoredPayload()
    {
        // The internal command scheduler serializes the command by its runtime type (rule 3), and an
        // explicit interface implementation is not a public property, so the row carries only the
        // command's own public properties.
        var json = JsonSerializer.Serialize(new ExpireWidgetTimeout(7), typeof(ExpireWidgetTimeout));

        json.Should().Be("{\"WidgetId\":7}");
    }

    private ProcessTimeoutScheduler CreateScheduler() => new(_internalCommandScheduler.Object, _timeProvider);

    /// <summary>A consumer-owned timeout command: stable name, ProcessId as an explicit implementation.</summary>
    [InternalCommandName("Tests.ExpireWidget")]
    private sealed record ExpireWidgetTimeout(int WidgetId) : IProcessTimeoutCommand<int>
    {
        int IProcessTimeoutCommand<int>.ProcessId => WidgetId;
    }
}

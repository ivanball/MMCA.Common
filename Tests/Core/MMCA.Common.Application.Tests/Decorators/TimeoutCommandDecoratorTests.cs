using AwesomeAssertions;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Application.UseCases.Markers;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

public sealed class TimeoutCommandDecoratorTests
{
    // ── A command without a budget keeps the caller's token untouched ──
    [Fact]
    public async Task HandleAsync_CommandWithoutTimeout_DelegatesWithCallerToken()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken token = cts.Token;

        var inner = new Mock<ICommandHandler<UnbudgetedCommand, Result>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<UnbudgetedCommand>(), token))
            .ReturnsAsync(Result.Success());

        var sut = new TimeoutCommandDecorator<UnbudgetedCommand, Result>(inner.Object);

        var result = await sut.HandleAsync(new UnbudgetedCommand(), token);

        result.IsSuccess.Should().BeTrue();
        inner.Verify(x => x.HandleAsync(It.IsAny<UnbudgetedCommand>(), token), Times.Once);
    }

    // ── A budget that is never exhausted is invisible ──
    [Fact]
    public async Task HandleAsync_WhenHandlerCompletesInsideBudget_ReturnsHandlerResult()
    {
        var inner = new Mock<ICommandHandler<BudgetedCommand, Result>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<BudgetedCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var sut = new TimeoutCommandDecorator<BudgetedCommand, Result>(inner.Object);

        var result = await sut.HandleAsync(new BudgetedCommand(TimeSpan.FromSeconds(30)));

        result.IsSuccess.Should().BeTrue();
    }

    // ── An expired budget becomes a failure result, not an exception ──
    [Fact]
    public async Task HandleAsync_WhenBudgetExpires_ReturnsTimedOutFailure()
    {
        var inner = new Mock<ICommandHandler<BudgetedCommand, Result>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<BudgetedCommand>(), It.IsAny<CancellationToken>()))
            .Returns<BudgetedCommand, CancellationToken>(async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
                return Result.Success();
            });

        var sut = new TimeoutCommandDecorator<BudgetedCommand, Result>(inner.Object);

        var result = await sut.HandleAsync(new BudgetedCommand(TimeSpan.FromMilliseconds(30)));

        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("Request.TimedOut");
        error.Source.Should().Be(nameof(BudgetedCommand));
    }

    // ── Cancellation raised by the CALLER stays an exception ──
    // Turning it into a failure result would report an aborted request as a server-side timeout and
    // hide the abort from every caller of the pipeline.
    [Fact]
    public async Task HandleAsync_WhenCallerCancels_RethrowsInsteadOfReturningFailure()
    {
        using var cts = new CancellationTokenSource();

        var inner = new Mock<ICommandHandler<BudgetedCommand, Result>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<BudgetedCommand>(), It.IsAny<CancellationToken>()))
            .Returns<BudgetedCommand, CancellationToken>(async (_, ct) =>
            {
                await cts.CancelAsync();
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
                return Result.Success();
            });

        var sut = new TimeoutCommandDecorator<BudgetedCommand, Result>(inner.Object);

        Func<Task> act = () => sut.HandleAsync(new BudgetedCommand(TimeSpan.FromMinutes(5)), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── A non-positive budget is treated as "no budget" ──
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task HandleAsync_WhenBudgetIsNotPositive_PassesThroughWithCallerToken(int seconds)
    {
        using var cts = new CancellationTokenSource();
        CancellationToken token = cts.Token;

        var inner = new Mock<ICommandHandler<BudgetedCommand, Result>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<BudgetedCommand>(), token))
            .ReturnsAsync(Result.Success());

        var sut = new TimeoutCommandDecorator<BudgetedCommand, Result>(inner.Object);

        var result = await sut.HandleAsync(new BudgetedCommand(TimeSpan.FromSeconds(seconds)), token);

        result.IsSuccess.Should().BeTrue();
        inner.Verify(x => x.HandleAsync(It.IsAny<BudgetedCommand>(), token), Times.Once);
    }

    // ── The failure factory also serves Result<T> ──
    [Fact]
    public async Task HandleAsync_WhenBudgetExpires_WithGenericResult_ReturnsFailure()
    {
        var inner = new Mock<ICommandHandler<BudgetedCommand, Result<int>>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<BudgetedCommand>(), It.IsAny<CancellationToken>()))
            .Returns<BudgetedCommand, CancellationToken>(async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
                return Result.Success(1);
            });

        var sut = new TimeoutCommandDecorator<BudgetedCommand, Result<int>>(inner.Object);

        var result = await sut.HandleAsync(new BudgetedCommand(TimeSpan.FromMilliseconds(30)));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("Request.TimedOut");
    }

    // Scrutor decorates handlers whose TResult is neither Result nor Result<T> too; the decorator
    // must resolve and pass through rather than fail at type-initialization time.
    [Fact]
    public async Task HandleAsync_NonResultTResult_UnbudgetedCommand_PassesThrough()
    {
        var inner = new Mock<ICommandHandler<UnbudgetedCommand, string>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<UnbudgetedCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("handled");

        var sut = new TimeoutCommandDecorator<UnbudgetedCommand, string>(inner.Object);

        var result = await sut.HandleAsync(new UnbudgetedCommand());

        result.Should().Be("handled");
    }
}

// ── Test types ──
public sealed record UnbudgetedCommand;

public sealed record BudgetedCommand(TimeSpan Timeout) : IHasTimeout;

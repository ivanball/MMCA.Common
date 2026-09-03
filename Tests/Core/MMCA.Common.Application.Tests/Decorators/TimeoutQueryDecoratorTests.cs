using AwesomeAssertions;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Application.UseCases.Markers;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

public sealed class TimeoutQueryDecoratorTests
{
    // ── A query without a budget keeps the caller's token untouched ──
    [Fact]
    public async Task HandleAsync_QueryWithoutTimeout_DelegatesWithCallerToken()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken token = cts.Token;

        var inner = new Mock<IQueryHandler<UnbudgetedQuery, Result<string>>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<UnbudgetedQuery>(), token))
            .ReturnsAsync(Result.Success("ok"));

        var sut = new TimeoutQueryDecorator<UnbudgetedQuery, Result<string>>(inner.Object);

        var result = await sut.HandleAsync(new UnbudgetedQuery(), token);

        result.IsSuccess.Should().BeTrue();
        inner.Verify(x => x.HandleAsync(It.IsAny<UnbudgetedQuery>(), token), Times.Once);
    }

    // ── A budget that is never exhausted is invisible ──
    [Fact]
    public async Task HandleAsync_WhenHandlerCompletesInsideBudget_ReturnsHandlerResult()
    {
        var inner = new Mock<IQueryHandler<BudgetedQuery, Result<string>>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<BudgetedQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success("ok"));

        var sut = new TimeoutQueryDecorator<BudgetedQuery, Result<string>>(inner.Object);

        var result = await sut.HandleAsync(new BudgetedQuery(TimeSpan.FromSeconds(30)));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("ok");
    }

    // ── An expired budget becomes a failure result, not an exception ──
    [Fact]
    public async Task HandleAsync_WhenBudgetExpires_ReturnsTimedOutFailure()
    {
        var inner = new Mock<IQueryHandler<BudgetedQuery, Result<string>>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<BudgetedQuery>(), It.IsAny<CancellationToken>()))
            .Returns<BudgetedQuery, CancellationToken>(async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
                return Result.Success("ok");
            });

        var sut = new TimeoutQueryDecorator<BudgetedQuery, Result<string>>(inner.Object);

        var result = await sut.HandleAsync(new BudgetedQuery(TimeSpan.FromMilliseconds(30)));

        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("Request.TimedOut");
        error.Source.Should().Be(nameof(BudgetedQuery));
    }

    // ── Cancellation raised by the CALLER stays an exception ──
    [Fact]
    public async Task HandleAsync_WhenCallerCancels_RethrowsInsteadOfReturningFailure()
    {
        using var cts = new CancellationTokenSource();

        var inner = new Mock<IQueryHandler<BudgetedQuery, Result<string>>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<BudgetedQuery>(), It.IsAny<CancellationToken>()))
            .Returns<BudgetedQuery, CancellationToken>(async (_, ct) =>
            {
                await cts.CancelAsync();
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
                return Result.Success("ok");
            });

        var sut = new TimeoutQueryDecorator<BudgetedQuery, Result<string>>(inner.Object);

        Func<Task> act = () => sut.HandleAsync(new BudgetedQuery(TimeSpan.FromMinutes(5)), cts.Token);

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

        var inner = new Mock<IQueryHandler<BudgetedQuery, Result<string>>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<BudgetedQuery>(), token))
            .ReturnsAsync(Result.Success("ok"));

        var sut = new TimeoutQueryDecorator<BudgetedQuery, Result<string>>(inner.Object);

        var result = await sut.HandleAsync(new BudgetedQuery(TimeSpan.FromSeconds(seconds)), token);

        result.IsSuccess.Should().BeTrue();
        inner.Verify(x => x.HandleAsync(It.IsAny<BudgetedQuery>(), token), Times.Once);
    }

    // Scrutor decorates handlers whose TResult is neither Result nor Result<T> too.
    [Fact]
    public async Task HandleAsync_NonResultTResult_UnbudgetedQuery_PassesThrough()
    {
        var inner = new Mock<IQueryHandler<UnbudgetedQuery, string>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<UnbudgetedQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("handled");

        var sut = new TimeoutQueryDecorator<UnbudgetedQuery, string>(inner.Object);

        var result = await sut.HandleAsync(new UnbudgetedQuery());

        result.Should().Be("handled");
    }
}

// ── Test types ──
public sealed record UnbudgetedQuery;

public sealed record BudgetedQuery(TimeSpan Timeout) : IHasTimeout;

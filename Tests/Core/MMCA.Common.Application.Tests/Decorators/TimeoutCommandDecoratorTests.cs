using AwesomeAssertions;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Application.UseCases.Markers;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

public sealed class TimeoutCommandDecoratorTests : TimeoutDecoratorTestsBase<Result>
{
    protected override string BudgetedRequestName => nameof(BudgetedCommand);

    protected override Result SuccessResult => Result.Success();

    // ── Command only: the failure factory also serves Result<T> ──
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

    protected override Func<CancellationToken, Task<T>> CreateSut<T>(
        TimeSpan? timeout,
        Func<CancellationToken, Task<T>> inner) => timeout is { } budget
            ? Wrap(new BudgetedCommand(budget), inner)
            : Wrap(new UnbudgetedCommand(), inner);

    private static Func<CancellationToken, Task<T>> Wrap<TCommand, T>(
        TCommand command,
        Func<CancellationToken, Task<T>> inner)
        where TCommand : class
    {
        var handler = new Mock<ICommandHandler<TCommand, T>>();
        handler.Setup(x => x.HandleAsync(It.IsAny<TCommand>(), It.IsAny<CancellationToken>()))
            .Returns<TCommand, CancellationToken>((_, ct) => inner(ct));

        var sut = new TimeoutCommandDecorator<TCommand, T>(handler.Object);

        return ct => sut.HandleAsync(command, ct);
    }
}

// ── Test types ──
public sealed record UnbudgetedCommand;

public sealed record BudgetedCommand(TimeSpan Timeout) : IHasTimeout;

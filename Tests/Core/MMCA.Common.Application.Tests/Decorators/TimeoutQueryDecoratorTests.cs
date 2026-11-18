using AwesomeAssertions;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Application.UseCases.Markers;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

public sealed class TimeoutQueryDecoratorTests : TimeoutDecoratorTestsBase<Result<string>>
{
    protected override string BudgetedRequestName => nameof(BudgetedQuery);

    protected override Result<string> SuccessResult => Result.Success("ok");

    protected override void AssertPassedThrough(Result<string> result)
    {
        base.AssertPassedThrough(result);
        result.Value.Should().Be("ok");
    }

    protected override Func<CancellationToken, Task<T>> CreateSut<T>(
        TimeSpan? timeout,
        Func<CancellationToken, Task<T>> inner) => timeout is { } budget
            ? Wrap(new BudgetedQuery(budget), inner)
            : Wrap(new UnbudgetedQuery(), inner);

    private static Func<CancellationToken, Task<T>> Wrap<TQuery, T>(
        TQuery query,
        Func<CancellationToken, Task<T>> inner)
        where TQuery : class
    {
        var handler = new Mock<IQueryHandler<TQuery, T>>();
        handler.Setup(x => x.HandleAsync(It.IsAny<TQuery>(), It.IsAny<CancellationToken>()))
            .Returns<TQuery, CancellationToken>((_, ct) => inner(ct));

        var sut = new TimeoutQueryDecorator<TQuery, T>(handler.Object);

        return ct => sut.HandleAsync(query, ct);
    }
}

// ── Test types ──
public sealed record UnbudgetedQuery;

public sealed record BudgetedQuery(TimeSpan Timeout) : IHasTimeout;

using AwesomeAssertions;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

public sealed class ProfilingQueryDecoratorTests : ProfilingDecoratorTestsBase<Result<string>>
{
    protected override Result<string> SuccessResult => Result.Success("data");

    protected override Result<string> FailureResult => Result.Failure<string>(Error.Failure("test", "failed"));

    protected override void AssertPassedThrough(Result<string> result)
    {
        base.AssertPassedThrough(result);
        result.Value.Should().Be("data");
    }

    protected override Func<CancellationToken, Task<Result<string>>> CreateSut(
        Func<CancellationToken, Task<Result<string>>> inner)
    {
        var handler = new Mock<IQueryHandler<ProfilingTestQuery, Result<string>>>();
        handler.Setup(x => x.HandleAsync(It.IsAny<ProfilingTestQuery>(), It.IsAny<CancellationToken>()))
            .Returns<ProfilingTestQuery, CancellationToken>((_, ct) => inner(ct));

        var sut = new ProfilingQueryDecorator<ProfilingTestQuery, Result<string>>(handler.Object);

        return ct => sut.HandleAsync(new ProfilingTestQuery(), ct);
    }
}

// ── Test type (must be public for Moq DynamicProxy) ──
public sealed record ProfilingTestQuery;

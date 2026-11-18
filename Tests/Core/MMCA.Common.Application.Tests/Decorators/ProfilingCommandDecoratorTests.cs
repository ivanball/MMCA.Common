using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

public sealed class ProfilingCommandDecoratorTests : ProfilingDecoratorTestsBase<Result>
{
    protected override Result SuccessResult => Result.Success();

    protected override Result FailureResult => Result.Failure(Error.Failure("test", "failed"));

    protected override Func<CancellationToken, Task<Result>> CreateSut(Func<CancellationToken, Task<Result>> inner)
    {
        var handler = new Mock<ICommandHandler<ProfilingTestCommand, Result>>();
        handler.Setup(x => x.HandleAsync(It.IsAny<ProfilingTestCommand>(), It.IsAny<CancellationToken>()))
            .Returns<ProfilingTestCommand, CancellationToken>((_, ct) => inner(ct));

        var sut = new ProfilingCommandDecorator<ProfilingTestCommand, Result>(handler.Object);

        return ct => sut.HandleAsync(new ProfilingTestCommand(), ct);
    }
}

// ── Test type (must be public for Moq DynamicProxy) ──
public sealed record ProfilingTestCommand;

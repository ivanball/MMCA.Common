using AwesomeAssertions;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

public sealed class ProfilingCommandDecoratorTests
{
    [Fact]
    public async Task HandleAsync_DelegatesCallToInnerHandler()
    {
        var inner = new Mock<ICommandHandler<ProfilingTestCommand, Result>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<ProfilingTestCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var sut = new ProfilingCommandDecorator<ProfilingTestCommand, Result>(inner.Object);

        var result = await sut.HandleAsync(new ProfilingTestCommand());

        result.IsSuccess.Should().BeTrue();
        inner.Verify(x => x.HandleAsync(It.IsAny<ProfilingTestCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WhenInnerFails_ReturnsFailure()
    {
        var inner = new Mock<ICommandHandler<ProfilingTestCommand, Result>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<ProfilingTestCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(Error.Failure("test", "failed")));

        var sut = new ProfilingCommandDecorator<ProfilingTestCommand, Result>(inner.Object);

        var result = await sut.HandleAsync(new ProfilingTestCommand());

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_PassesCancellationTokenToInner()
    {
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        var inner = new Mock<ICommandHandler<ProfilingTestCommand, Result>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<ProfilingTestCommand>(), token))
            .ReturnsAsync(Result.Success());

        var sut = new ProfilingCommandDecorator<ProfilingTestCommand, Result>(inner.Object);

        await sut.HandleAsync(new ProfilingTestCommand(), token);

        inner.Verify(x => x.HandleAsync(It.IsAny<ProfilingTestCommand>(), token), Times.Once);
    }
}

// ── Test type (must be public for Moq DynamicProxy) ──
public sealed record ProfilingTestCommand;

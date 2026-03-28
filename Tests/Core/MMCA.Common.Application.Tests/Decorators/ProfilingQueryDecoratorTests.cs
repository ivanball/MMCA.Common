using AwesomeAssertions;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

public sealed class ProfilingQueryDecoratorTests
{
    [Fact]
    public async Task HandleAsync_DelegatesCallToInnerHandler()
    {
        var inner = new Mock<IQueryHandler<ProfilingTestQuery, Result<string>>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<ProfilingTestQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success("data"));

        var sut = new ProfilingQueryDecorator<ProfilingTestQuery, Result<string>>(inner.Object);

        var result = await sut.HandleAsync(new ProfilingTestQuery());

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("data");
        inner.Verify(x => x.HandleAsync(It.IsAny<ProfilingTestQuery>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WhenInnerFails_ReturnsFailure()
    {
        var inner = new Mock<IQueryHandler<ProfilingTestQuery, Result<string>>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<ProfilingTestQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<string>(Error.Failure("test", "failed")));

        var sut = new ProfilingQueryDecorator<ProfilingTestQuery, Result<string>>(inner.Object);

        var result = await sut.HandleAsync(new ProfilingTestQuery());

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_PassesCancellationTokenToInner()
    {
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        var inner = new Mock<IQueryHandler<ProfilingTestQuery, Result<string>>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<ProfilingTestQuery>(), token))
            .ReturnsAsync(Result.Success("ok"));

        var sut = new ProfilingQueryDecorator<ProfilingTestQuery, Result<string>>(inner.Object);

        await sut.HandleAsync(new ProfilingTestQuery(), token);

        inner.Verify(x => x.HandleAsync(It.IsAny<ProfilingTestQuery>(), token), Times.Once);
    }
}

// ── Test type (must be public for Moq DynamicProxy) ──
public sealed record ProfilingTestQuery;

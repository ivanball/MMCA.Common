using FluentAssertions;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

public sealed class LoggingCommandDecoratorTests
{
    // ── Mocks ──
    private sealed record Mocks(
        Mock<ICommandHandler<TestLoggingCommand, Result>> Inner,
        Mock<ICorrelationContext> CorrelationContext,
        Mock<ILogger<LoggingCommandDecorator<TestLoggingCommand, Result>>> Logger);

    // ── Factory ──
    private static (LoggingCommandDecorator<TestLoggingCommand, Result> Sut, Mocks Mocks) CreateSut()
    {
        var inner = new Mock<ICommandHandler<TestLoggingCommand, Result>>();
        var correlationContext = new Mock<ICorrelationContext>();
        correlationContext.Setup(x => x.CorrelationId).Returns("test-correlation-id");
        var logger = new Mock<ILogger<LoggingCommandDecorator<TestLoggingCommand, Result>>>();

        var sut = new LoggingCommandDecorator<TestLoggingCommand, Result>(
            inner.Object,
            correlationContext.Object,
            logger.Object);

        var mocks = new Mocks(inner, correlationContext, logger);

        return (sut, mocks);
    }

    // ── HandleAsync: delegates to inner handler ──
    [Fact]
    public async Task HandleAsync_DelegatesCallToInnerHandler()
    {
        var (sut, mocks) = CreateSut();
        mocks.Inner.Setup(x => x.HandleAsync(It.IsAny<TestLoggingCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        await sut.HandleAsync(new TestLoggingCommand());

        mocks.Inner.Verify(
            x => x.HandleAsync(It.IsAny<TestLoggingCommand>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── HandleAsync: inner succeeds, returns result ──
    [Fact]
    public async Task HandleAsync_WhenInnerSucceeds_ReturnsResult()
    {
        var (sut, mocks) = CreateSut();
        mocks.Inner.Setup(x => x.HandleAsync(It.IsAny<TestLoggingCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var result = await sut.HandleAsync(new TestLoggingCommand());

        result.IsSuccess.Should().BeTrue();
    }

    // ── HandleAsync: inner throws, rethrows exception ──
    [Fact]
    public async Task HandleAsync_WhenInnerThrows_RethrowsException()
    {
        var (sut, mocks) = CreateSut();
        mocks.Inner.Setup(x => x.HandleAsync(It.IsAny<TestLoggingCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("test error"));

        var act = () => sut.HandleAsync(new TestLoggingCommand());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("test error");
    }
}

// ── Test type (must be public for Moq DynamicProxy) ──
public sealed record TestLoggingCommand;

using FluentAssertions;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

public sealed class LoggingQueryDecoratorTests
{
    // ── Mocks ──
    private sealed record Mocks(
        Mock<IQueryHandler<TestLoggingQuery, Result<string>>> Inner,
        Mock<ICorrelationContext> CorrelationContext,
        Mock<ILogger<LoggingQueryDecorator<TestLoggingQuery, Result<string>>>> Logger);

    // ── Factory ──
    private static (LoggingQueryDecorator<TestLoggingQuery, Result<string>> Sut, Mocks Mocks) CreateSut()
    {
        var inner = new Mock<IQueryHandler<TestLoggingQuery, Result<string>>>();
        var correlationContext = new Mock<ICorrelationContext>();
        correlationContext.Setup(x => x.CorrelationId).Returns("test-correlation-id");
        var logger = new Mock<ILogger<LoggingQueryDecorator<TestLoggingQuery, Result<string>>>>();

        var sut = new LoggingQueryDecorator<TestLoggingQuery, Result<string>>(
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
        mocks.Inner.Setup(x => x.HandleAsync(It.IsAny<TestLoggingQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success("data"));

        await sut.HandleAsync(new TestLoggingQuery());

        mocks.Inner.Verify(
            x => x.HandleAsync(It.IsAny<TestLoggingQuery>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── HandleAsync: inner succeeds, returns result ──
    [Fact]
    public async Task HandleAsync_WhenInnerSucceeds_ReturnsResult()
    {
        var (sut, mocks) = CreateSut();
        mocks.Inner.Setup(x => x.HandleAsync(It.IsAny<TestLoggingQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success("data"));

        var result = await sut.HandleAsync(new TestLoggingQuery());

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("data");
    }

    // ── HandleAsync: inner throws, rethrows exception ──
    [Fact]
    public async Task HandleAsync_WhenInnerThrows_RethrowsException()
    {
        var (sut, mocks) = CreateSut();
        mocks.Inner.Setup(x => x.HandleAsync(It.IsAny<TestLoggingQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("test error"));

        var act = () => sut.HandleAsync(new TestLoggingQuery());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("test error");
    }
}

// ── Test type (must be public for Moq DynamicProxy) ──
public sealed record TestLoggingQuery;

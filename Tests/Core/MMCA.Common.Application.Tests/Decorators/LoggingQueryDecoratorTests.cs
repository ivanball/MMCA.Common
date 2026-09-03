using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Tests.Fakes.Billing.Domain;
using MMCA.Common.Application.UseCases.Contracts;
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

    // ── HandleAsync: the logging scope carries query name, module and correlation id ──
    [Fact]
    public async Task HandleAsync_BeginsScopeWithQueryNameModuleAndCorrelationId()
    {
        var (sut, logger) = CreateSutWithCapturingLogger<BillingFakeQuery>();

        await sut.HandleAsync(new BillingFakeQuery());

        var scope = logger.Scopes.Should().ContainSingle().Subject;
        scope.Should().ContainSingle(entry => entry.Key == "QueryName")
            .Which.Value.Should().Be(nameof(BillingFakeQuery));
        scope.Should().ContainSingle(entry => entry.Key == "ModuleName")
            .Which.Value.Should().Be(
                "Billing",
                "the module comes from the workspace App.Module.Layer namespace convention, so log queries can filter by module");
        scope.Should().ContainSingle(entry => entry.Key == "CorrelationId")
            .Which.Value.Should().Be("test-correlation-id");
    }

    // ── HandleAsync: a query outside a module namespace still gets a module value ──
    [Fact]
    public async Task HandleAsync_WhenQueryNamespaceCarriesNoModule_ScopeModuleIsUnknown()
    {
        var (sut, logger) = CreateSutWithCapturingLogger<TestLoggingQuery>();

        await sut.HandleAsync(new TestLoggingQuery());

        var scope = logger.Scopes.Should().ContainSingle().Subject;
        scope.Should().ContainSingle(entry => entry.Key == "ModuleName")
            .Which.Value.Should().Be(
                "unknown",
                "an unresolvable module must still produce the key, so the scope shape never varies between handlers");
    }

    // ── Factory: real (capturing) logger, so the scope payload can be inspected ──
    private static (LoggingQueryDecorator<TQuery, Result<string>> Sut, ScopeCapturingLogger<LoggingQueryDecorator<TQuery, Result<string>>> Logger) CreateSutWithCapturingLogger<TQuery>()
        where TQuery : class
    {
        var inner = new Mock<IQueryHandler<TQuery, Result<string>>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<TQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success("data"));
        var correlationContext = new Mock<ICorrelationContext>();
        correlationContext.Setup(x => x.CorrelationId).Returns("test-correlation-id");
        var logger = new ScopeCapturingLogger<LoggingQueryDecorator<TQuery, Result<string>>>();

        var sut = new LoggingQueryDecorator<TQuery, Result<string>>(
            inner.Object,
            correlationContext.Object,
            logger);

        return (sut, logger);
    }

    private sealed class ScopeCapturingLogger<TCategoryName> : ILogger<TCategoryName>
    {
        public List<IReadOnlyList<KeyValuePair<string, object?>>> Scopes { get; } = [];

        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                Scopes.Add(values);
            }

            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Messages.Add(formatter(state, exception));
        }
    }
}

// ── Test type (must be public for Moq DynamicProxy) ──
public sealed record TestLoggingQuery;

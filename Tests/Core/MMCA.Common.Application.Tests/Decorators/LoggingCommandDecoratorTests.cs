using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Tests.Fakes.Billing.Domain;
using MMCA.Common.Application.UseCases.Contracts;
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

    // ── HandleAsync: inner returns failure Result ──
    [Fact]
    public async Task HandleAsync_WhenInnerReturnsFailure_ReturnsFailureResult()
    {
        var (sut, mocks) = CreateSut();
        var failureResult = Result.Failure(Error.Validation("Test.Error", "something went wrong"));
        mocks.Inner.Setup(x => x.HandleAsync(It.IsAny<TestLoggingCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(failureResult);

        var result = await sut.HandleAsync(new TestLoggingCommand());

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("Test.Error");
    }

    // ── HandleAsync: the logging scope carries command name, module and correlation id ──
    [Fact]
    public async Task HandleAsync_BeginsScopeWithCommandNameModuleAndCorrelationId()
    {
        var (sut, logger) = CreateSutWithCapturingLogger<BillingFakeCommand>();

        await sut.HandleAsync(new BillingFakeCommand());

        var scope = logger.Scopes.Should().ContainSingle().Subject;
        scope.Should().ContainSingle(entry => entry.Key == "CommandName")
            .Which.Value.Should().Be(nameof(BillingFakeCommand));
        scope.Should().ContainSingle(entry => entry.Key == "ModuleName")
            .Which.Value.Should().Be(
                "Billing",
                "the module comes from the workspace App.Module.Layer namespace convention, so log queries can filter by module");
        scope.Should().ContainSingle(entry => entry.Key == "CorrelationId")
            .Which.Value.Should().Be("test-correlation-id");
    }

    // ── HandleAsync: a command outside a module namespace still gets a module value ──
    [Fact]
    public async Task HandleAsync_WhenCommandNamespaceCarriesNoModule_ScopeModuleIsUnknown()
    {
        var (sut, logger) = CreateSutWithCapturingLogger<TestLoggingCommand>();

        await sut.HandleAsync(new TestLoggingCommand());

        var scope = logger.Scopes.Should().ContainSingle().Subject;
        scope.Should().ContainSingle(entry => entry.Key == "ModuleName")
            .Which.Value.Should().Be(
                "unknown",
                "an unresolvable module must still produce the key, so the scope shape never varies between handlers");
    }

    // ── Factory: real (capturing) logger, so the scope payload can be inspected ──
    private static (LoggingCommandDecorator<TCommand, Result> Sut, ScopeCapturingLogger<LoggingCommandDecorator<TCommand, Result>> Logger) CreateSutWithCapturingLogger<TCommand>()
        where TCommand : class
    {
        var inner = new Mock<ICommandHandler<TCommand, Result>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<TCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        var correlationContext = new Mock<ICorrelationContext>();
        correlationContext.Setup(x => x.CorrelationId).Returns("test-correlation-id");
        var logger = new ScopeCapturingLogger<LoggingCommandDecorator<TCommand, Result>>();

        var sut = new LoggingCommandDecorator<TCommand, Result>(
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
public sealed record TestLoggingCommand;

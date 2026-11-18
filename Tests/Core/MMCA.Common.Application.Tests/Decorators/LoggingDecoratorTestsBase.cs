using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

/// <summary>
/// The logging decorator contract shared by the command and the query pipeline: delegate once,
/// pass the result through, rethrow, open one scope carrying the request name, module and
/// correlation id, and record an exception as a Warning outcome line without its stack. Each
/// subclass supplies the decorator under test and its two request types.
/// </summary>
/// <typeparam name="TResult">The result type the subclass's handlers return.</typeparam>
public abstract class LoggingDecoratorTestsBase<TResult>
    where TResult : Result
{
    protected const string CorrelationId = "test-correlation-id";

    /// <summary>Which request type the subclass builds the decorator for.</summary>
    protected enum RequestKind
    {
        /// <summary>A request outside any App.Module.Layer namespace.</summary>
        NoModule,

        /// <summary>A request in the Billing fake module namespace.</summary>
        BillingModule,
    }

    /// <summary>The scope key naming the request: CommandName or QueryName.</summary>
    protected abstract string RequestNameScopeKey { get; }

    /// <summary>The type name of the <see cref="RequestKind.NoModule"/> request.</summary>
    protected abstract string NoModuleRequestName { get; }

    /// <summary>The type name of the <see cref="RequestKind.BillingModule"/> request.</summary>
    protected abstract string BillingModuleRequestName { get; }

    /// <summary>The successful result the inner handler returns.</summary>
    protected abstract TResult SuccessResult { get; }

    /// <summary>
    /// Builds the decorator over an inner handler that answers with <paramref name="inner"/>, and
    /// returns a call that sends it one request of <paramref name="kind"/>.
    /// </summary>
    protected abstract Func<Task<TResult>> CreateSut(
        RequestKind kind,
        Func<Task<TResult>> inner,
        ICorrelationContext correlationContext,
        ILogger sink);

    /// <summary>Extra checks on a successful pass-through (the query also checks the value).</summary>
    protected virtual void AssertPassedThrough(TResult result) => result.IsSuccess.Should().BeTrue();

    [Fact]
    public async Task HandleAsync_DelegatesCallToInnerHandler()
    {
        var calls = 0;
        var handle = CreateSut(
            RequestKind.NoModule,
            () =>
            {
                calls++;
                return Task.FromResult(SuccessResult);
            });

        await handle();

        calls.Should().Be(1);
    }

    [Fact]
    public async Task HandleAsync_WhenInnerSucceeds_ReturnsResult()
    {
        var handle = CreateSut(RequestKind.NoModule, () => Task.FromResult(SuccessResult));

        var result = await handle();

        AssertPassedThrough(result);
    }

    [Fact]
    public async Task HandleAsync_WhenInnerThrows_RethrowsException()
    {
        var handle = CreateSut(
            RequestKind.NoModule,
            () => Task.FromException<TResult>(new InvalidOperationException("test error")));

        await handle.Should().ThrowAsync<InvalidOperationException>().WithMessage("test error");
    }

    [Fact]
    public async Task HandleAsync_BeginsScopeWithRequestNameModuleAndCorrelationId()
    {
        var logger = new CapturingLogger();
        var handle = CreateSut(RequestKind.BillingModule, () => Task.FromResult(SuccessResult), logger);

        await handle();

        var scope = logger.Scopes.Should().ContainSingle().Subject;
        scope.Should().ContainSingle(entry => entry.Key == RequestNameScopeKey)
            .Which.Value.Should().Be(BillingModuleRequestName);
        scope.Should().ContainSingle(entry => entry.Key == "ModuleName")
            .Which.Value.Should().Be(
                "Billing",
                "the module comes from the workspace App.Module.Layer namespace convention, so log queries can filter by module");
        scope.Should().ContainSingle(entry => entry.Key == "CorrelationId")
            .Which.Value.Should().Be(CorrelationId);
    }

    [Fact]
    public async Task HandleAsync_WhenRequestNamespaceCarriesNoModule_ScopeModuleIsUnknown()
    {
        var logger = new CapturingLogger();
        var handle = CreateSut(RequestKind.NoModule, () => Task.FromResult(SuccessResult), logger);

        await handle();

        var scope = logger.Scopes.Should().ContainSingle().Subject;
        scope.Should().ContainSingle(entry => entry.Key == "ModuleName")
            .Which.Value.Should().Be(
                "unknown",
                "an unresolvable module must still produce the key, so the scope shape never varies between handlers");
    }

    // The exception outcome line is the pipeline's own record, not a second full-stack Error. The
    // boundary (GlobalExceptionHandler, or the internal-command processor) logs the exception once,
    // with its stack; logging it here too produced two Error rows and two stacks for every
    // unhandled exception.
    [Fact]
    public async Task HandleAsync_WhenInnerThrows_LogsTheOutcomeAtWarningWithoutTheException()
    {
        var logger = new CapturingLogger();
        var handle = CreateSut(
            RequestKind.NoModule,
            () => Task.FromException<TResult>(new InvalidOperationException("test error")),
            logger);

        await handle.Should().ThrowAsync<InvalidOperationException>();

        logger.Entries.Should().NotContain(
            entry => entry.Level == LogLevel.Error,
            "the boundary owns the single Error row for an unhandled exception");

        var outcome = logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning).Subject;
        outcome.Exception.Should().BeNull("a second stack for the same failure doubles ingestion cost for no diagnostic gain");
        outcome.Message.Should().Contain(NoModuleRequestName)
            .And.Contain(nameof(InvalidOperationException))
            .And.Contain(CorrelationId)
            .And.Contain("propagates to the boundary");
    }

    /// <summary>Builds the decorator with a fixed correlation id and a logger nobody inspects.</summary>
    protected Func<Task<TResult>> CreateSut(RequestKind kind, Func<Task<TResult>> inner) =>
        CreateSut(kind, inner, new CapturingLogger());

    private Func<Task<TResult>> CreateSut(RequestKind kind, Func<Task<TResult>> inner, ILogger sink)
    {
        var correlationContext = new Mock<ICorrelationContext>();
        correlationContext.Setup(x => x.CorrelationId).Returns(CorrelationId);
        return CreateSut(kind, inner, correlationContext.Object, sink);
    }

    /// <summary>One captured log line: the level it was written at, the exception it carried, its text.</summary>
    protected sealed record LogEntry(LogLevel Level, Exception? Exception, string Message);

    /// <summary>Records every scope and line, so the scope payload and outcome can be inspected.</summary>
    protected sealed class CapturingLogger : ILogger
    {
        public List<IReadOnlyList<KeyValuePair<string, object?>>> Scopes { get; } = [];

        public List<LogEntry> Entries { get; } = [];

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
            Entries.Add(new LogEntry(logLevel, exception, formatter(state, exception)));
        }
    }

    /// <summary>
    /// Adapts the shared sink to the <see cref="ILogger{TCategoryName}"/> a decorator's constructor
    /// asks for.
    /// </summary>
    /// <typeparam name="TCategoryName">The decorator type the logger is categorised under.</typeparam>
    protected sealed class ForwardingLogger<TCategoryName>(ILogger sink) : ILogger<TCategoryName>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => sink.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => sink.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            sink.Log(logLevel, eventId, state, exception, formatter);
    }
}

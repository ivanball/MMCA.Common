using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.DomainEvents;
using MMCA.Common.Domain.DomainEvents;

namespace MMCA.Common.Application.Tests.DomainEvents;

/// <summary>
/// The base class logs the failure and then lets it propagate. Swallowing made the
/// "retried via the outbox" promise false: a handler reported success, its outbox row was marked
/// processed, and the side effect was lost. Propagation hands the decision to the delivery
/// mechanism (outbox retry/dead-letter, or broker redelivery).
/// </summary>
public sealed class SafeDomainEventHandlerTests
{
    // ── Successful handling ──
    [Fact]
    public async Task HandleAsync_WhenHandlerSucceeds_CompletesWithoutException()
    {
        var logger = new RecordingLogger();
        var sut = new TestSafeDomainEventHandler(logger, shouldThrow: false);
        var domainEvent = new TestSafeDomainEvent("test-data");

        await FluentActions.Invoking(() => sut.HandleAsync(domainEvent))
            .Should().NotThrowAsync();

        sut.WasHandled.Should().BeTrue();
        logger.Errors.Should().BeEmpty();
    }

    // ── Exception logged AND propagated (was: caught and swallowed) ──
    [Fact]
    public async Task HandleAsync_WhenHandlerThrows_LogsAndPropagates()
    {
        var logger = new RecordingLogger();
        var sut = new TestSafeDomainEventHandler(logger, shouldThrow: true);
        var domainEvent = new TestSafeDomainEvent("test-data");

        (await FluentActions.Invoking(() => sut.HandleAsync(domainEvent))
                .Should().ThrowAsync<InvalidOperationException>(
                    "the delivery mechanism, not the handler, decides what happens to a failed event"))
            .WithMessage("Handler failed");

        sut.WasHandled.Should().BeTrue();
        logger.Errors.Should().ContainSingle()
            .Which.Should().BeOfType<InvalidOperationException>();
    }

    // ── The log is written before the exception reaches the caller ──
    [Fact]
    public async Task HandleAsync_WhenHandlerThrows_WritesTheLogBeforeTheCallerSeesTheException()
    {
        var logger = new RecordingLogger();
        var sut = new TestSafeDomainEventHandler(logger, shouldThrow: true);
        var domainEvent = new TestSafeDomainEvent("test-data");

        try
        {
            await sut.HandleAsync(domainEvent);
        }
        catch (InvalidOperationException)
        {
            logger.Sequence.Add("caught");
        }

        logger.Sequence.Should().Equal(
            ["logged", "caught"],
            "an exception filter logs on the first pass, so the handler context is recorded even if an outer frame rethrows or wraps");
    }

    // ── OperationCanceledException still passes straight through, unlogged ──
    [Fact]
    public async Task HandleAsync_WhenOperationCanceledException_PropagatesWithoutLogging()
    {
        var logger = new RecordingLogger();
        var sut = new TestSafeDomainEventHandler(logger, throwCancellation: true);
        var domainEvent = new TestSafeDomainEvent("test-data");

        await FluentActions.Invoking(() => sut.HandleAsync(domainEvent))
            .Should().ThrowAsync<OperationCanceledException>();

        logger.Errors.Should().BeEmpty("host shutdown is not a delivery failure and must not be logged as one");
        logger.Sequence.Should().BeEmpty();
    }
}

// ── Test helpers ──
public sealed record TestSafeDomainEvent(string Data) : BaseDomainEvent;

/// <summary>
/// Captures the logged exceptions and the order in which the log write happened relative to
/// other observable steps, so the tests can assert the exception-filter ordering.
/// </summary>
public sealed class RecordingLogger : ILogger
{
    public List<Exception> Errors { get; } = [];

    public List<string> Sequence { get; } = [];

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel != LogLevel.Error)
        {
            return;
        }

        Sequence.Add("logged");
        if (exception is not null)
        {
            Errors.Add(exception);
        }
    }
}

public sealed class TestSafeDomainEventHandler : SafeDomainEventHandler<TestSafeDomainEvent>
{
    private readonly bool _shouldThrow;
    private readonly bool _throwCancellation;

    public bool WasHandled { get; private set; }

    public TestSafeDomainEventHandler(ILogger logger, bool shouldThrow = false, bool throwCancellation = false)
        : base(logger)
    {
        _shouldThrow = shouldThrow;
        _throwCancellation = throwCancellation;
    }

    protected override Task HandleSafelyAsync(TestSafeDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        WasHandled = true;

        if (_throwCancellation)
        {
            throw new OperationCanceledException();
        }

        if (_shouldThrow)
        {
            throw new InvalidOperationException("Handler failed");
        }

        return Task.CompletedTask;
    }
}

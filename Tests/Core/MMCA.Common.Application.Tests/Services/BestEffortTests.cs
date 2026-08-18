using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Services;

namespace MMCA.Common.Application.Tests.Services;

/// <summary>
/// Unit tests for <see cref="BestEffort"/>: the success path is transparent, a failure is swallowed
/// with exactly one Warning and one metric increment, and cancellation is deliberately NOT part of
/// the swallow.
/// </summary>
public sealed class BestEffortTests
{
    [Fact]
    public async Task ExecuteAsync_OnSuccess_RunsTheActionAndLogsNothing()
    {
        var logger = new RecordingLogger();
        var ran = false;

        await BestEffort.ExecuteAsync("noop", logger, _ =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        ran.Should().BeTrue();
        logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_PassesTheCallersTokenToTheAction()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken observed = default;

        await BestEffort.ExecuteAsync("noop", new RecordingLogger(), ct =>
        {
            observed = ct;
            return Task.CompletedTask;
        }, cts.Token);

        observed.Should().Be(cts.Token);
    }

    [Fact]
    public async Task ExecuteAsync_OnFailure_SwallowsAndLogsOneWarningNamingTheOperation()
    {
        var logger = new RecordingLogger();

        var act = async () => await BestEffort.ExecuteAsync(
            "output-cache-evict",
            logger,
            _ => throw new InvalidOperationException("downstream is down"));

        await act.Should().NotThrowAsync();
        logger.Warnings.Should().ContainSingle().Which.Should().Contain("output-cache-evict");
        logger.Exceptions.Should().ContainSingle().Which.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task ExecuteAsync_OnFailure_CountsTheFailureWithAnOperationTag()
    {
        var measurements = new List<(long Value, string? Operation)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "MMCA.Common.BestEffort" && instrument.Name == "besteffort.dispatch.failed")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? operation = null;
            foreach (var tag in tags)
            {
                if (string.Equals(tag.Key, "operation", StringComparison.Ordinal))
                {
                    operation = tag.Value as string;
                }
            }

            measurements.Add((value, operation));
        });
        listener.Start();

        await BestEffort.ExecuteAsync("metered-op", new RecordingLogger(), _ => throw new TimeoutException());

        listener.Dispose();

        measurements.Where(m => m.Operation == "metered-op")
            .Should().ContainSingle().Which.Value.Should().Be(1);
    }

    // Cancellation is a shutdown/abandonment signal, not a side-effect failure: swallowing it would
    // turn an orderly stop into a silent warning and let the caller keep going.
    [Fact]
    public async Task ExecuteAsync_WhenTheCallersTokenIsCancelled_Rethrows()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var logger = new RecordingLogger();

        var act = async () => await BestEffort.ExecuteAsync(
            "cancelled-op",
            logger,
            ct => Task.FromCanceled(ct),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        logger.Warnings.Should().BeEmpty(because: "a cancellation is not a best-effort failure");
    }

    // A cancellation that is NOT the caller's (an inner timeout, say) is still a failure of the side
    // effect, so it is swallowed like any other.
    [Fact]
    public async Task ExecuteAsync_WhenTheActionCancelsOnItsOwn_SwallowsIt()
    {
        var logger = new RecordingLogger();

        var act = async () => await BestEffort.ExecuteAsync(
            "inner-timeout",
            logger,
            _ => Task.FromCanceled(new CancellationToken(canceled: true)));

        await act.Should().NotThrowAsync();
        logger.Warnings.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteAsync_ValidatesItsArguments()
    {
        var blankOperation = async () => await BestEffort.ExecuteAsync(" ", new RecordingLogger(), _ => Task.CompletedTask);
        var nullLogger = async () => await BestEffort.ExecuteAsync("op", null!, _ => Task.CompletedTask);
        var nullAction = async () => await BestEffort.ExecuteAsync("op", new RecordingLogger(), null!);

        await blankOperation.Should().ThrowAsync<ArgumentException>();
        await nullLogger.Should().ThrowAsync<ArgumentNullException>();
        await nullAction.Should().ThrowAsync<ArgumentNullException>();
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public List<Exception?> Exceptions { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
                Exceptions.Add(exception);
            }
        }
    }
}

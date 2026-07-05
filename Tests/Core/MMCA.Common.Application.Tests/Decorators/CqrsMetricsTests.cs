using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

/// <summary>
/// Captures the RED metrics emitted by <c>CqrsMetrics</c> through the logging decorators using a
/// <see cref="MeterListener"/> (the meter and its instruments are internal statics, so they are
/// observed exactly the way OpenTelemetry exporters observe them). Asserts the instrument names,
/// the ms unit, and the command/query + outcome tag pairs for the success, business-failure, and
/// exception paths. Measurements are filtered by this file's probe types because the meter is a
/// process-wide static shared with tests running in parallel.
/// </summary>
public sealed class CqrsMetricsTests
{
    // Duplicated literal: CqrsMetrics.MeterName is internal (same duplication the Aspire package
    // makes). If the meter is ever renamed this test fails, which is the point: exporters
    // subscribe by this exact name.
    private const string MeterName = "MMCA.Common.Cqrs";

    private sealed record CapturedMeasurement(
        string InstrumentName,
        string? Unit,
        double Value,
        Dictionary<string, object?> Tags);

    // ── Capture helper ──
    private static async Task<List<CapturedMeasurement>> CaptureAsync(
        Func<Task> act,
        string tagKey,
        string tagValue)
    {
        var gate = new System.Threading.Lock();
        var captured = new List<CapturedMeasurement>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (string.Equals(instrument.Meter.Name, MeterName, StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            var tagDictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                tagDictionary[tag.Key] = tag.Value;
            }

            lock (gate)
            {
                captured.Add(new CapturedMeasurement(instrument.Name, instrument.Unit, value, tagDictionary));
            }
        });
        listener.Start();

        await act();

        lock (gate)
        {
            return [.. captured.Where(m => m.Tags.TryGetValue(tagKey, out var v) && Equals(v, tagValue))];
        }
    }

    // ── Factories ──
    private static (LoggingCommandDecorator<CqrsMetricsProbeCommand, Result> Sut,
        Mock<ICommandHandler<CqrsMetricsProbeCommand, Result>> Inner) CreateCommandSut()
    {
        var inner = new Mock<ICommandHandler<CqrsMetricsProbeCommand, Result>>();
        var correlationContext = new Mock<ICorrelationContext>();
        correlationContext.Setup(x => x.CorrelationId).Returns("metrics-correlation-id");
        var logger = new Mock<ILogger<LoggingCommandDecorator<CqrsMetricsProbeCommand, Result>>>();

        var sut = new LoggingCommandDecorator<CqrsMetricsProbeCommand, Result>(
            inner.Object, correlationContext.Object, logger.Object);
        return (sut, inner);
    }

    private static (LoggingQueryDecorator<CqrsMetricsProbeQuery, Result<string>> Sut,
        Mock<IQueryHandler<CqrsMetricsProbeQuery, Result<string>>> Inner) CreateQuerySut()
    {
        var inner = new Mock<IQueryHandler<CqrsMetricsProbeQuery, Result<string>>>();
        var correlationContext = new Mock<ICorrelationContext>();
        correlationContext.Setup(x => x.CorrelationId).Returns("metrics-correlation-id");
        var logger = new Mock<ILogger<LoggingQueryDecorator<CqrsMetricsProbeQuery, Result<string>>>>();

        var sut = new LoggingQueryDecorator<CqrsMetricsProbeQuery, Result<string>>(
            inner.Object, correlationContext.Object, logger.Object);
        return (sut, inner);
    }

    // ── Command duration: success ──
    [Fact]
    public async Task CommandDuration_OnSuccess_RecordsCompletedOutcomeWithCommandTag()
    {
        var (sut, inner) = CreateCommandSut();
        inner.Setup(x => x.HandleAsync(It.IsAny<CqrsMetricsProbeCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var measurements = await CaptureAsync(
            () => sut.HandleAsync(new CqrsMetricsProbeCommand()),
            "command",
            nameof(CqrsMetricsProbeCommand));

        var measurement = measurements.Should().ContainSingle().Which;
        measurement.InstrumentName.Should().Be("cqrs.command.duration");
        measurement.Unit.Should().Be("ms");
        measurement.Value.Should().BeGreaterThanOrEqualTo(0);
        measurement.Tags.Should().Contain("outcome", "completed");
    }

    // ── Command duration: business failure ──
    [Fact]
    public async Task CommandDuration_OnFailureResult_RecordsFailedOutcome()
    {
        var (sut, inner) = CreateCommandSut();
        inner.Setup(x => x.HandleAsync(It.IsAny<CqrsMetricsProbeCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(Error.Validation("Test.Error", "invalid input")));

        var measurements = await CaptureAsync(
            () => sut.HandleAsync(new CqrsMetricsProbeCommand()),
            "command",
            nameof(CqrsMetricsProbeCommand));

        measurements.Should().ContainSingle()
            .Which.Tags.Should().Contain("outcome", "failed");
    }

    // ── Command duration: exception ──
    [Fact]
    public async Task CommandDuration_OnException_RecordsExceptionOutcomeAndRethrows()
    {
        var (sut, inner) = CreateCommandSut();
        inner.Setup(x => x.HandleAsync(It.IsAny<CqrsMetricsProbeCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("handler blew up"));

        var measurements = await CaptureAsync(
            async () =>
            {
                var invoke = () => sut.HandleAsync(new CqrsMetricsProbeCommand());
                await invoke.Should().ThrowAsync<InvalidOperationException>();
            },
            "command",
            nameof(CqrsMetricsProbeCommand));

        measurements.Should().ContainSingle()
            .Which.Tags.Should().Contain("outcome", "exception");
    }

    // ── Query duration: success ──
    [Fact]
    public async Task QueryDuration_OnSuccess_RecordsCompletedOutcomeWithQueryTag()
    {
        var (sut, inner) = CreateQuerySut();
        inner.Setup(x => x.HandleAsync(It.IsAny<CqrsMetricsProbeQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success("data"));

        var measurements = await CaptureAsync(
            () => sut.HandleAsync(new CqrsMetricsProbeQuery()),
            "query",
            nameof(CqrsMetricsProbeQuery));

        var measurement = measurements.Should().ContainSingle().Which;
        measurement.InstrumentName.Should().Be("cqrs.query.duration");
        measurement.Unit.Should().Be("ms");
        measurement.Value.Should().BeGreaterThanOrEqualTo(0);
        measurement.Tags.Should().Contain("outcome", "completed");
    }

    // ── Query duration: business failure ──
    [Fact]
    public async Task QueryDuration_OnFailureResult_RecordsFailedOutcome()
    {
        // The query decorator inspects the Result the same way the command decorator does, so a
        // business failure (Result.IsFailure) records outcome=failed rather than being conflated
        // with genuine successes. Only an unhandled exception records outcome=exception.
        var (sut, inner) = CreateQuerySut();
        inner.Setup(x => x.HandleAsync(It.IsAny<CqrsMetricsProbeQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<string>(Error.NotFoundError("Test.Missing", "not found")));

        var measurements = await CaptureAsync(
            () => sut.HandleAsync(new CqrsMetricsProbeQuery()),
            "query",
            nameof(CqrsMetricsProbeQuery));

        measurements.Should().ContainSingle()
            .Which.Tags.Should().Contain("outcome", "failed");
    }

    // ── Query duration: exception ──
    [Fact]
    public async Task QueryDuration_OnException_RecordsExceptionOutcomeAndRethrows()
    {
        var (sut, inner) = CreateQuerySut();
        inner.Setup(x => x.HandleAsync(It.IsAny<CqrsMetricsProbeQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("handler blew up"));

        var measurements = await CaptureAsync(
            async () =>
            {
                var invoke = () => sut.HandleAsync(new CqrsMetricsProbeQuery());
                await invoke.Should().ThrowAsync<InvalidOperationException>();
            },
            "query",
            nameof(CqrsMetricsProbeQuery));

        measurements.Should().ContainSingle()
            .Which.Tags.Should().Contain("outcome", "exception");
    }
}

// ── Probe types (public for Moq DynamicProxy; unique to this file so tag filtering isolates
// these measurements from parallel tests sharing the static meter) ──
public sealed record CqrsMetricsProbeCommand;

public sealed record CqrsMetricsProbeQuery;

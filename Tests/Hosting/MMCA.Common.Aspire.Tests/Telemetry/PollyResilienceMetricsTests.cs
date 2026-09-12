using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;

namespace MMCA.Common.Aspire.Tests.Telemetry;

/// <summary>
/// The Polly resilience meter (ADR-009). The standard resilience handler sits on every HttpClient and
/// every gRPC typed client, so its retries, timeouts and circuit transitions are the only in-process
/// evidence that an inter-service call is degrading. The counter is always exported; the two duration
/// HISTOGRAMS are a cost knob (rubric §31) a host opts into with
/// <c>Telemetry:EnablePollyDurationMetrics=true</c>.
/// <para>
/// The instrument names are Polly's own, verified against the pinned Polly.Extensions assembly:
/// <c>resilience.polly.strategy.events</c>, <c>resilience.polly.strategy.attempt.duration</c> and
/// <c>resilience.polly.pipeline.duration</c> on a meter named <c>Polly</c>. If Polly ever renames one,
/// these tests go red rather than the export silently going quiet.
/// </para>
/// </summary>
public sealed class PollyResilienceMetricsTests
{
    private const string Key = Extensions.EnablePollyDurationMetricsConfigKey;

    // ── The opt-in knob helper ──
    [Fact]
    public void Absent_LeavesTheDurationHistogramsOff()
        => Extensions.IsInstrumentationEnabled(Config(null), Key)
            .Should().BeFalse("an unset opt-in knob must stay off (the default)");

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    public void True_TurnsTheDurationHistogramsOn(string raw)
        => Extensions.IsInstrumentationEnabled(Config(raw), Key).Should().BeTrue();

    [Theory]
    [InlineData("false")]
    [InlineData("1")] // not a bool literal, so it must not opt in
    [InlineData("yes")] // unparseable
    [InlineData("")] // blank
    public void FalseOrUnparseable_LeavesTheDurationHistogramsOff(string raw)
        => Extensions.IsInstrumentationEnabled(Config(raw), Key).Should().BeFalse();

    // ── What actually reaches an exporter ──
    [Fact]
    public void StrategyEventsCounter_IsExported_WithoutAnyOptIn()
    {
        var exported = CollectFrom([]);

        exported.Should().Contain(
            Extensions.PollyStrategyEventsInstrument,
            because: "a retry or an opened circuit is the signal the meter exists for, and it is never dropped");
    }

    [Theory]
    [InlineData(Extensions.PollyAttemptDurationInstrument)]
    [InlineData(Extensions.PollyPipelineDurationInstrument)]
    public void DurationHistograms_AreDropped_ByDefault(string instrument)
    {
        var exported = CollectFrom([]);

        exported.Should().NotContain(
            instrument,
            because: "the per-bucket duration streams re-measure what http.client.request.duration already reports");
    }

    [Theory]
    [InlineData(Extensions.PollyAttemptDurationInstrument)]
    [InlineData(Extensions.PollyPipelineDurationInstrument)]
    public void DurationHistograms_AreExported_WhenTheHostOptsIn(string instrument)
    {
        var exported = CollectFrom(new() { [Key] = "true" });

        exported.Should().Contain(instrument);
    }

    [Fact]
    public void TheDropView_LeavesOtherMetersAlone()
    {
        var exported = CollectFrom([]);

        exported.Should().Contain(
            "some.other.duration",
            because: "the View must match on the Polly meter, not on every instrument whose name ends in duration");
    }

    private static IConfiguration Config(string? value)
    {
        var values = new Dictionary<string, string?>();
        if (value is not null)
        {
            values[Key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    /// <summary>
    /// Builds a MeterProvider through the real <c>ConfigureOpenTelemetry()</c> path, emits one
    /// measurement on each of Polly's three instruments (plus a same-shaped instrument on a different
    /// meter, as a control), and returns the instrument names that reached the exporter.
    /// </summary>
    private static IReadOnlyList<string> CollectFrom(Dictionary<string, string?> settings)
    {
        // Blanked explicitly so an OTLP endpoint or Application Insights key in the developer's own
        // environment cannot attach a second exporter to this provider.
        settings["OTEL_EXPORTER_OTLP_ENDPOINT"] = string.Empty;
        settings["APPLICATIONINSIGHTS_CONNECTION_STRING"] = string.Empty;

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.ConfigureOpenTelemetry();

        var exportedMetrics = new List<Metric>();
        builder.Services.ConfigureOpenTelemetryMeterProvider(metrics => metrics
            .AddMeter("MMCA.Common.Aspire.Tests.PollyControl")
            .AddInMemoryExporter(exportedMetrics));

        using var host = builder.Build();
        var provider = host.Services.GetRequiredService<MeterProvider>();

        // The real Polly meter name, so this exercises the AddMeter + View the framework registered
        // rather than a stand-in. Polly itself emits these from Polly.Extensions' telemetry listener.
        using var polly = new Meter(Extensions.PollyMeterName);
        polly.CreateCounter<long>(Extensions.PollyStrategyEventsInstrument).Add(1);
        polly.CreateHistogram<double>(Extensions.PollyAttemptDurationInstrument).Record(1.0);
        polly.CreateHistogram<double>(Extensions.PollyPipelineDurationInstrument).Record(1.0);

        using var control = new Meter("MMCA.Common.Aspire.Tests.PollyControl");
        control.CreateHistogram<double>("some.other.duration").Record(1.0);

        provider.ForceFlush();

        return [.. exportedMetrics.Select(m => m.Name)];
    }
}

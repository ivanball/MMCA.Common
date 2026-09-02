using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;

namespace MMCA.Common.Aspire.Tests.Telemetry;

/// <summary>
/// The metrics cost knobs (rubric §31): a deployed host drops the two highest-volume, lowest-value
/// AppMetrics families by setting <c>Telemetry:DisableHttpClientMetrics=true</c> and/or
/// <c>Telemetry:DisableRuntimeMetrics=true</c>. Anything other than a parseable boolean <see langword="true"/>
/// keeps the instrumentation, so a typo can never silently blind a whole metric family.
/// </summary>
public sealed class MetricsInstrumentationToggleTests
{
    private const string Key = "Telemetry:DisableRuntimeMetrics";

    private static IConfiguration Config(string? value)
    {
        var values = new Dictionary<string, string?>();
        if (value is not null)
        {
            values[Key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void Absent_KeepsInstrumentation()
        => Extensions.IsInstrumentationDisabled(Config(null), Key)
            .Should().BeFalse("an unset knob must keep the instrumentation (the default)");

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    public void True_DropsInstrumentation(string raw)
        => Extensions.IsInstrumentationDisabled(Config(raw), Key).Should().BeTrue();

    [Theory]
    [InlineData("false")]
    [InlineData("0")] // not a bool literal — must not disable
    [InlineData("yes")] // unparseable
    [InlineData("")] // blank
    public void FalseOrUnparseable_KeepsInstrumentation(string raw)
        => Extensions.IsInstrumentationDisabled(Config(raw), Key).Should().BeFalse();

    // ── The knobs must be authoritative, not advisory ──
    // Reading the flag correctly is only half the contract. Until 2026-09-02 the toggle merely skipped
    // AddHttpClientInstrumentation(), and a deployed host ALSO calls UseAzureMonitor(), whose distro
    // adds the System.Net.Http meter itself: http.client.open_connections kept flowing and stayed the
    // single largest AppMetrics stream in both production workspaces with the toggle switched on. The
    // fix is a metrics View, which applies to the whole MeterProvider no matter who added the meter.
    // These tests reproduce that exact shape: a third party subscribes the meter, and the assertion is
    // whether the instrument reaches an exporter.
    [Theory]
    [InlineData("System.Net.Http", "http.client.open_connections")]
    [InlineData("System.Net.NameResolution", "dns.lookup.duration")]
    public void HttpClientMetricsDisabled_DropsTheStream_EvenWhenAnotherComponentAddsTheMeter(
        string meterName,
        string instrumentName)
    {
        var exported = CollectFrom(
            new() { ["Telemetry:DisableHttpClientMetrics"] = "true" },
            meterName,
            instrumentName);

        exported.Should().NotContain(
            instrumentName,
            because: "a View drops the instrument regardless of which component subscribed the meter");
    }

    [Fact]
    public void HttpClientMetricsEnabled_KeepsTheStream()
    {
        var exported = CollectFrom([], "System.Net.Http", "http.client.open_connections");

        exported.Should().Contain(
            "http.client.open_connections",
            because: "an unset knob must change nothing (the default)");
    }

    [Fact]
    public void RuntimeMetricsDisabled_DropsTheStream_EvenWhenAnotherComponentAddsTheMeter()
    {
        var exported = CollectFrom(
            new() { ["Telemetry:DisableRuntimeMetrics"] = "true" },
            "System.Runtime",
            "dotnet.gc.collections");

        exported.Should().NotContain("dotnet.gc.collections");
    }

    [Fact]
    public void RuntimeMetricsEnabled_KeepsTheStream()
    {
        var exported = CollectFrom([], "System.Runtime", "dotnet.gc.collections");

        exported.Should().Contain("dotnet.gc.collections");
    }

    /// <summary>
    /// Builds a MeterProvider through the real <c>ConfigureOpenTelemetry()</c> path, has a third party
    /// subscribe <paramref name="meterName"/> exactly the way the Azure Monitor distro does, emits one
    /// measurement on <paramref name="instrumentName"/>, and returns the instrument names that reached
    /// the exporter.
    /// </summary>
    private static IReadOnlyList<string> CollectFrom(
        Dictionary<string, string?> settings,
        string meterName,
        string instrumentName)
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
            .AddMeter(meterName)
            .AddInMemoryExporter(exportedMetrics));

        using var host = builder.Build();
        var provider = host.Services.GetRequiredService<MeterProvider>();

        using var meter = new Meter(meterName);
        meter.CreateCounter<long>(instrumentName).Add(1);

        provider.ForceFlush();

        return [.. exportedMetrics.Select(m => m.Name)];
    }
}

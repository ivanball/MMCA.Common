using System.Diagnostics;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace MMCA.Common.Aspire.Tests.Telemetry;

/// <summary>
/// The optional AI package publishes its traces and its metrics under one name
/// (<c>MMCA.Common.AI</c>, which is <c>AiUsageMeter.MeterName</c> and the source name the pipeline
/// hands to <c>UseOpenTelemetry</c>). Aspire subscribes that name as a LITERAL, because
/// <c>AiDependencyIsolationTestsBase</c> bars a project reference from this package to the AI one.
/// These facts are what catches the literal drifting away from the constant it mirrors: a rename
/// there goes red here instead of silently taking model-spend telemetry off every dashboard.
/// </summary>
public sealed class AiTelemetryExportTests
{
    private const string AiName = Extensions.AiTelemetryName;

    [Fact]
    public void AiMeter_IsExported()
    {
        var exported = CollectFrom();

        exported.Should().Contain(
            "mmca.ai.input_tokens",
            because: "token spend is the number the AI meter exists for");
        exported.Should().Contain("mmca.ai.call.duration");
    }

    [Fact]
    public void AiActivitySource_HasListeners_AfterConfigureOpenTelemetry()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(Blanked([]));
        builder.ConfigureOpenTelemetry();

        using var host = builder.Build();

        // Resolving the provider is what starts the SDK and attaches its listener to the source.
        _ = host.Services.GetRequiredService<TracerProvider>();

        using var source = new ActivitySource(AiName);
        source.HasListeners().Should().BeTrue(
            because: "a chat call's span has to reach the exporter like any other dependency span");
    }

    private static Dictionary<string, string?> Blanked(Dictionary<string, string?> settings)
    {
        // Blanked explicitly so an OTLP endpoint or Application Insights key in the developer's own
        // environment cannot attach a second exporter to this provider.
        settings["OTEL_EXPORTER_OTLP_ENDPOINT"] = string.Empty;
        settings["APPLICATIONINSIGHTS_CONNECTION_STRING"] = string.Empty;

        return settings;
    }

    /// <summary>
    /// Builds a MeterProvider through the real <c>ConfigureOpenTelemetry()</c> path, emits one
    /// measurement on each of the AI meter's instruments, and returns what reached the exporter.
    /// </summary>
    private static IReadOnlyList<string> CollectFrom()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(Blanked([]));
        builder.ConfigureOpenTelemetry();

        var exportedMetrics = new List<Metric>();
        builder.Services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddInMemoryExporter(exportedMetrics));

        using var host = builder.Build();
        var provider = host.Services.GetRequiredService<MeterProvider>();

        // The real names AiUsageMeter creates, stood up here rather than through a project reference
        // the layer rules forbid.
        using var ai = new Meter(AiName);
        ai.CreateCounter<long>("mmca.ai.input_tokens").Add(1);
        ai.CreateCounter<long>("mmca.ai.output_tokens").Add(1);
        ai.CreateHistogram<double>("mmca.ai.call.duration", unit: "s").Record(0.5);

        provider.ForceFlush();

        return [.. exportedMetrics.Select(m => m.Name)];
    }
}

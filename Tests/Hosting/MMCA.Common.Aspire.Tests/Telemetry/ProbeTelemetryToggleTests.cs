using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.Http;
using OpenTelemetry.Trace;

namespace MMCA.Common.Aspire.Tests.Telemetry;

/// <summary>
/// The <c>Telemetry:FilterProbeTelemetry</c> knob asserted through the real
/// <c>ConfigureOpenTelemetry()</c> path rather than inferred from the flag reader: all three pieces
/// (the inbound request filter, the outbound HttpClient filter, and the descendant-suppressing
/// processor) must appear together when the knob is left at its default and disappear together when
/// a host sets it to <see langword="false"/>.
/// </summary>
public sealed class ProbeTelemetryToggleTests
{
    [Fact]
    public void Default_InstallsBothInstrumentationFilters()
    {
        using var host = BuildHost(knob: null, out _, out var sourceName);
        var (aspNetFilter, httpClientFilter) = InstrumentationFilters(host);

        aspNetFilter.Should().NotBeNull("probe requests must be refused before a span is exported");
        httpClientFilter.Should().NotBeNull("YARP and gateway probe calls have no request ancestor");
        sourceName.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Disabled_InstallsNeitherInstrumentationFilter()
    {
        using var host = BuildHost(knob: "false", out _, out _);
        var (aspNetFilter, httpClientFilter) = InstrumentationFilters(host);

        aspNetFilter.Should().BeNull("an opted-out host keeps its probe traces");
        httpClientFilter.Should().BeNull("an opted-out host keeps its probe traces");
    }

    [Fact]
    public void Default_DropsAProbeRequestAndItsDependency()
    {
        var exported = Trace(knob: null);

        exported.Should().BeEmpty(
            "the probe span and the SELECT 1 hanging off it are the AppRequests / AppDependencies volume this knob exists to remove");
    }

    [Fact]
    public void Disabled_KeepsAProbeRequestAndItsDependency()
    {
        var exported = Trace(knob: "false");

        exported.Should().HaveCount(2, "with the knob off nothing suppresses probe traces");
    }

    /// <summary>
    /// Emits a probe server span with one dependency child through a TracerProvider built by the
    /// real service defaults, and returns the display names that reached an exporter.
    /// </summary>
    private static IReadOnlyList<string> Trace(string? knob)
    {
        using var host = BuildHost(knob, out var exported, out var sourceName);
        var provider = host.Services.GetRequiredService<TracerProvider>();

        using (var source = new ActivitySource(sourceName))
        {
            using var request = source.StartActivity(
                "GET",
                ActivityKind.Server,
                default(ActivityContext),
                [new KeyValuePair<string, object?>("url.path", "/alive")]);
            using var dependency = source.StartActivity("SELECT 1", ActivityKind.Client);
        }

        provider.ForceFlush();

        return [.. exported.Select(activity => activity.DisplayName)];
    }

    private static (Delegate? AspNetCore, Delegate? HttpClient) InstrumentationFilters(IHost host)
    {
        // Building the provider is what runs the deferred tracer-builder callbacks that configure
        // the instrumentation options.
        _ = host.Services.GetRequiredService<TracerProvider>();

        var aspNetCore = host.Services
            .GetRequiredService<IOptionsMonitor<AspNetCoreTraceInstrumentationOptions>>()
            .Get(Options.DefaultName);
        var httpClient = host.Services
            .GetRequiredService<IOptionsMonitor<HttpClientTraceInstrumentationOptions>>()
            .Get(Options.DefaultName);

        return (aspNetCore.Filter, httpClient.FilterHttpRequestMessage);
    }

    private static IHost BuildHost(string? knob, out List<Activity> exported, out string sourceName)
    {
        sourceName = "Test.ProbeToggle." + Guid.NewGuid().ToString("N");

        var settings = new Dictionary<string, string?>
        {
            // Blanked explicitly so an OTLP endpoint or Application Insights key in the developer's
            // own environment cannot attach a second exporter to this provider.
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = string.Empty,
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = string.Empty,
        };

        if (knob is not null)
        {
            settings["Telemetry:FilterProbeTelemetry"] = knob;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.ConfigureOpenTelemetry();

        var collected = new List<Activity>();
        exported = collected;

        var registeredSource = sourceName;
        builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing
            .AddSource(registeredSource)
            .AddInMemoryExporter(collected));

        return builder.Build();
    }
}

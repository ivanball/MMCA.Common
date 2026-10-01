using AwesomeAssertions;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MMCA.Common.Aspire.Tests.Telemetry;

/// <summary>
/// Pins the Live Metrics off switch a deployed host relies on (rubric §31). Live Metrics is on by
/// default in the Azure Monitor distro and keeps a replica talking to the Live Metrics service even
/// when nobody has the blade open, which is enough background CPU and inbound traffic to hold an idle
/// Container Apps replica above the 0.01-core / 1,000 bytes-per-second line that bills it at the idle
/// rate. The switch is the distro's own <c>AzureMonitor</c> configuration section
/// (<c>AzureMonitor__EnableLiveMetrics=false</c> as an environment variable), so there is no MMCA knob
/// to keep in step; these tests fail if a distro upgrade stops honoring it through
/// <c>ConfigureOpenTelemetry()</c>.
/// </summary>
public sealed class LiveMetricsConfigurationTests
{
    [Fact]
    public void Unset_LeavesLiveMetricsOn()
        => ResolveOptions([]).EnableLiveMetrics
            .Should().BeTrue("the distro default is on, and an unset key must change nothing");

    [Fact]
    public void ConfiguredOff_ReachesTheDistroOptions()
        => ResolveOptions(new() { ["AzureMonitor:EnableLiveMetrics"] = "false" }).EnableLiveMetrics
            .Should().BeFalse("a deployed host turns Live Metrics off through the distro's own section");

    private static AzureMonitorOptions ResolveOptions(Dictionary<string, string?> settings)
    {
        // A syntactically valid connection string is what makes ConfigureOpenTelemetry() call
        // UseAzureMonitor(); the host is built but never started, so nothing is sent.
        settings["APPLICATIONINSIGHTS_CONNECTION_STRING"] =
            "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://localhost/";
        settings["OTEL_EXPORTER_OTLP_ENDPOINT"] = string.Empty;

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.ConfigureOpenTelemetry();

        using var host = builder.Build();
        return host.Services.GetRequiredService<IOptionsMonitor<AzureMonitorOptions>>().CurrentValue;
    }
}

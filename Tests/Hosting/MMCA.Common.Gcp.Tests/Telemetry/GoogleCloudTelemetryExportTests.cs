using System.Diagnostics;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMCA.Common.Aspire;
using MMCA.Common.Gcp.Telemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace MMCA.Common.Gcp.Tests.Telemetry;

/// <summary>
/// The wire round trip without Docker: a real host on <c>AddServiceDefaults()</c> plus
/// <c>AddGoogleCloudTelemetryExporter()</c>, pointed at an in-process OTLP/HTTP receiver through
/// <c>GoogleCloudTelemetry:Endpoint</c>, emits a span and a log record and force-flushes. Both arrive
/// at the OTLP paths with the bearer token and the quota project, carrying <c>gcp.project_id</c> on
/// the resource; metrics never reach this exporter.
/// </summary>
public sealed class GoogleCloudTelemetryExportTests
{
    private const string ProjectId = "mmca-telemetry-tests";
    private const int FlushTimeoutMilliseconds = 10_000;

    [Fact]
    public async Task ASpanAndALogRecord_ReachTheTelemetryApi_WithTheGoogleHeaders()
    {
        await using var receiver = await OtlpTestReceiver.StartAsync();
        const string applicationName = "mmca-gcp-telemetry-export";
        using var host = BuildHost(applicationName, receiver.BaseAddress, ProjectId);
        await host.StartAsync(TestContext.Current.CancellationToken);

        Emit(host, applicationName);
        Flush(host);
        await host.StopAsync(TestContext.Current.CancellationToken);

        var requests = receiver.Requests.ToList();
        var traces = requests.Where(request => request.Path == "/v1/traces").ToList();
        var logs = requests.Where(request => request.Path == "/v1/logs").ToList();
        traces.Should().NotBeEmpty("the span must be exported to /v1/traces");
        logs.Should().NotBeEmpty("the log record must be exported to /v1/logs");
        requests.Should().NotContain(request => request.Path == "/v1/metrics", "metrics are left to the collector path");

        foreach (var request in traces.Concat(logs))
        {
            request.Authorization.Should().Be("Bearer export-token");
            request.QuotaProject.Should().Be(ProjectId);
            request.ContentType.Should().Be("application/x-protobuf");
            Contains(request.Body, "gcp.project_id").Should().BeTrue("the resource maps the data to the project");
            Contains(request.Body, ProjectId).Should().BeTrue();
        }

        traces.Should().Contain(request => Contains(request.Body, "gcp-telemetry-span"));
        logs.Should().Contain(request => Contains(request.Body, "gcp-telemetry-log-record"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WithoutAProjectId_NothingIsExported(string projectId)
    {
        await using var receiver = await OtlpTestReceiver.StartAsync();
        const string applicationName = "mmca-gcp-telemetry-noop";
        using var host = BuildHost(applicationName, receiver.BaseAddress, projectId);
        await host.StartAsync(TestContext.Current.CancellationToken);

        Emit(host, applicationName);
        Flush(host);
        await host.StopAsync(TestContext.Current.CancellationToken);

        receiver.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutCredentials_TheHostRuns_AndNoUnauthenticatedRequestIsSent()
    {
        await using var receiver = await OtlpTestReceiver.StartAsync();
        const string applicationName = "mmca-gcp-telemetry-no-credentials";
        using var host = BuildHost(applicationName, receiver.BaseAddress, ProjectId, token: null);
        await host.StartAsync(TestContext.Current.CancellationToken);

        Emit(host, applicationName);
        host.Services.GetRequiredService<TracerProvider>().ForceFlush(FlushTimeoutMilliseconds);
        host.Services.GetRequiredService<LoggerProvider>().ForceFlush(FlushTimeoutMilliseconds);
        await host.StopAsync(TestContext.Current.CancellationToken);

        receiver.Requests.Should().BeEmpty("an export without a token is refused before it leaves the process");
    }

    private static IHost BuildHost(string applicationName, Uri endpoint, string projectId, string? token = "export-token")
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ApplicationName = applicationName });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = string.Empty,
            ["GoogleCloudTelemetry:ProjectId"] = projectId,
            ["GoogleCloudTelemetry:Endpoint"] = endpoint.ToString(),
        });
        builder.Services.AddSingleton<IGoogleCloudTelemetryTokenSource>(new FakeTelemetryTokenSource(token));
        builder.AddServiceDefaults();
        builder.AddGoogleCloudTelemetryExporter();
        return builder.Build();
    }

    private static void Emit(IHost host, string applicationName)
    {
        using (var source = new ActivitySource(applicationName))
        {
            using var activity = source.StartActivity("gcp-telemetry-span");
            activity.Should().NotBeNull("service defaults subscribe the application's own source");
        }

        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MMCA.Common.Gcp.Tests.Telemetry");
        logger.LogWarning("gcp-telemetry-log-record");
    }

    private static void Flush(IHost host)
    {
        host.Services.GetRequiredService<TracerProvider>().ForceFlush(FlushTimeoutMilliseconds).Should().BeTrue();
        host.Services.GetRequiredService<LoggerProvider>().ForceFlush(FlushTimeoutMilliseconds).Should().BeTrue();
    }

    private static bool Contains(byte[] body, string text) =>
        body.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;
}

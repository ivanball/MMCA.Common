using System.Diagnostics;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMCA.Common.Aspire;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace MMCA.Common.Aws.Tests.Telemetry;

/// <summary>
/// Item 32, the wire round trip without AWS: a real host on <c>AddServiceDefaults()</c> plus
/// <c>AddAwsTelemetryExporter()</c>, with both signal endpoints overridden to an in-process OTLP/HTTP
/// receiver and static placeholder keys, emits a span and a log record and force-flushes. Both arrive
/// SigV4-signed for their own service, the log request carries the log group and stream, and metrics
/// never reach this exporter. This proves signing shape, headers, gating and wiring only; acceptance
/// by live X-Ray and CloudWatch Logs is not exercised.
/// </summary>
public sealed class AwsTelemetryExportTests
{
    private const string Region = "us-east-1";
    private const int FlushTimeoutMilliseconds = 10_000;

    [Fact]
    public async Task ASpanAndALogRecord_ReachTheReceiver_Signed()
    {
        await using var receiver = await AwsOtlpTestReceiver.StartAsync();
        const string applicationName = "mmca-aws-telemetry-export";
        using var telemetryEvents = new OpenTelemetryEventCollector();
        using var host = BuildHost(applicationName, receiver.BaseAddress, Region);
        await host.StartAsync(TestContext.Current.CancellationToken);

        var emitted = Emit(host, applicationName);
        Flush(host);
        await host.StopAsync(TestContext.Current.CancellationToken);

        var requests = receiver.Requests.ToList();
        var traces = requests.Where(request => request.Path == "/v1/traces").ToList();
        var logs = requests.Where(request => request.Path == "/v1/logs").ToList();

        // This test has failed in CI with no request at the traces endpoint and no reason in the log:
        // the SDK reports an export failure only on its own event sources. The details say which side
        // lost the span the next time: the span itself, what the receiver saw, and those events.
        var details =
            $"{emitted}; receiver saw [{string.Join(", ", requests.Select(request => request.Path))}]; OpenTelemetry events: {telemetryEvents.Describe()}";
        traces.Should().NotBeEmpty("the span must be exported to the traces endpoint ({0})", details);
        logs.Should().NotBeEmpty("the log record must be exported to the logs endpoint ({0})", details);
        requests.Should().NotContain(request => request.Path == "/v1/metrics", "metrics are left to an ADOT collector");

        foreach (var request in traces)
        {
            request.Header("Authorization").Should().StartWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/")
                .And.Contain($"/{Region}/xray/aws4_request");
            request.Header("X-Amz-Date").Should().NotBeEmpty();
            request.ContentType.Should().Be("application/x-protobuf");
        }

        foreach (var request in logs)
        {
            request.Header("Authorization").Should().StartWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/")
                .And.Contain($"/{Region}/logs/aws4_request")
                .And.Contain("x-aws-log-group")
                .And.Contain("x-aws-log-stream");
            request.Header("x-aws-log-group").Should().Be("mmca-group");
            request.Header("x-aws-log-stream").Should().Be("mmca-stream");
            request.ContentType.Should().Be("application/x-protobuf");
        }

        traces.Should().Contain(request => Contains(request.Body, "aws-telemetry-span"));
        logs.Should().Contain(request => Contains(request.Body, "aws-telemetry-log-record"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WithoutARegion_NothingIsExported(string region)
    {
        await using var receiver = await AwsOtlpTestReceiver.StartAsync();
        const string applicationName = "mmca-aws-telemetry-noop";
        using var host = BuildHost(applicationName, receiver.BaseAddress, region);
        await host.StartAsync(TestContext.Current.CancellationToken);

        Emit(host, applicationName);
        Flush(host);
        await host.StopAsync(TestContext.Current.CancellationToken);

        receiver.Requests.Should().BeEmpty();
    }

    private static IHost BuildHost(string applicationName, Uri receiver, string region)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ApplicationName = applicationName });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = string.Empty,
            ["AwsTelemetry:Region"] = region,
            ["AwsTelemetry:TracesEndpoint"] = new Uri(receiver, "v1/traces").ToString(),
            ["AwsTelemetry:LogsEndpoint"] = new Uri(receiver, "v1/logs").ToString(),
            ["AwsTelemetry:LogGroup"] = "mmca-group",
            ["AwsTelemetry:LogStream"] = "mmca-stream",
            ["AwsTelemetry:AccessKeyId"] = "AKIDEXAMPLE",
            ["AwsTelemetry:SecretAccessKey"] = "secret",
        });
        builder.AddServiceDefaults();
        builder.AddAwsTelemetryExporter();
        return builder.Build();
    }

    /// <summary>Emits one span and one log record, and describes the span for a failure message.</summary>
    private static string Emit(IHost host, string applicationName)
    {
        var ambient = Activity.Current;
        string description;
        using (var source = new ActivitySource(applicationName))
        {
            using var activity = source.StartActivity("aws-telemetry-span");
            activity.Should().NotBeNull("service defaults subscribe the application's own source");
            var ambientDescription = ambient is null
                ? "none"
                : $"{ambient.Source.Name}/{ambient.OperationName} recorded {ambient.Recorded}";
            description = $"span recorded: {activity?.Recorded}, ambient activity: {ambientDescription}";
        }

        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MMCA.Common.Aws.Tests.Telemetry");
        logger.LogWarning("aws-telemetry-log-record");
        return description;
    }

    private static void Flush(IHost host)
    {
        host.Services.GetRequiredService<TracerProvider>().ForceFlush(FlushTimeoutMilliseconds).Should().BeTrue();
        host.Services.GetRequiredService<LoggerProvider>().ForceFlush(FlushTimeoutMilliseconds).Should().BeTrue();
    }

    private static bool Contains(byte[] body, string text) =>
        body.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;

    /// <summary>
    /// Collects the OpenTelemetry SDK's own warnings and errors (its event sources are the only place an
    /// export failure is reported) for a failure message. Process-wide, so it can also hold events of a
    /// test running beside this one.
    /// </summary>
    private sealed class OpenTelemetryEventCollector : System.Diagnostics.Tracing.EventListener
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _events = new();

        public string Describe() => _events.IsEmpty ? "none" : string.Join(" | ", _events);

        protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource eventSource)
        {
            if (eventSource.Name.StartsWith("OpenTelemetry-", StringComparison.Ordinal))
            {
                EnableEvents(eventSource, System.Diagnostics.Tracing.EventLevel.Warning);
            }
        }

        protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs eventData)
        {
            var payload = eventData.Payload is null ? string.Empty : string.Join(", ", eventData.Payload);
            _events.Enqueue($"{eventData.EventSource.Name}/{eventData.EventName}: {payload}");
        }
    }
}

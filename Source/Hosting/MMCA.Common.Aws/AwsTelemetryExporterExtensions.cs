using Amazon;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMCA.Common.Aspire.Integrations;
using MMCA.Common.Aws.Telemetry;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace MMCA.Common.Aws;

/// <summary>
/// Registration extension that exports traces and logs straight to the AWS X-Ray and CloudWatch Logs
/// OTLP endpoints beside the vendor-neutral OTLP exporter <c>AddServiceDefaults()</c> configures: the
/// AWS twin of <c>AddAzureMonitorExporter()</c> and <c>AddGoogleCloudTelemetryExporter()</c>, with no
/// collector sidecar.
/// </summary>
public static class AwsTelemetryExporterExtensions
{
    /// <summary>The SigV4 service name of the X-Ray OTLP traces endpoint.</summary>
    internal const string TracesService = "xray";

    /// <summary>The SigV4 service name of the CloudWatch Logs OTLP logs endpoint.</summary>
    internal const string LogsService = "logs";

    /// <summary>The header naming the CloudWatch Logs log group.</summary>
    internal const string LogGroupHeader = "x-aws-log-group";

    /// <summary>The header naming the CloudWatch Logs log stream.</summary>
    internal const string LogStreamHeader = "x-aws-log-stream";

    extension<TBuilder>(TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        /// <summary>
        /// Exports traces, and logs when a log group and stream are configured, to the AWS X-Ray and
        /// CloudWatch Logs OTLP endpoints (OTLP over HTTP/protobuf, signed with AWS Signature Version 4)
        /// when <c>AwsTelemetry:Region</c> is present. When absent or whitespace this method registers
        /// nothing, so local development and tests take no AWS dependency at startup.
        /// <para>
        /// Configuration keys:
        /// <list type="bullet">
        ///   <item><c>AwsTelemetry:Region</c> (the gate): the region written to, for example
        ///     <c>eu-west-1</c>. It is stamped on the resource as <c>cloud.region</c>, beside
        ///     <c>cloud.provider = aws</c>.</item>
        ///   <item><c>AwsTelemetry:LogGroup</c> and <c>AwsTelemetry:LogStream</c> (optional, together):
        ///     the CloudWatch Logs destination. Without them no log is exported.</item>
        ///   <item><c>AwsTelemetry:TracesEndpoint</c> and <c>AwsTelemetry:LogsEndpoint</c> (optional):
        ///     the full signal URLs, defaulting to <c>https://xray.{region}.amazonaws.com/v1/traces</c>
        ///     and <c>https://logs.{region}.amazonaws.com/v1/logs</c>.</item>
        ///   <item><c>AwsTelemetry:AccessKeyId</c> and <c>AwsTelemetry:SecretAccessKey</c> (optional,
        ///     together): static credentials. Without them the AWS default credential chain (an IAM
        ///     role on ECS, EKS or EC2, the environment, the shared profile) is resolved on the first
        ///     export, never at registration. The identity needs permission to write to X-Ray and to the
        ///     log group.</item>
        /// </list>
        /// </para>
        /// <para>
        /// Metrics are NOT exported by this call: one request is capped at 1,000 data points, and the
        /// OpenTelemetry .NET SDK offers no supported way to split one metric's points across requests.
        /// Ship metrics through an AWS Distro for OpenTelemetry (ADOT) collector instead, by pointing the
        /// vendor-neutral <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> at it.
        /// </para>
        /// <para>
        /// What is proven: the signature, headers, gating and wiring are covered by tests against the
        /// SDK signer and a loopback receiver. The call has not been exercised against live X-Ray or
        /// CloudWatch Logs endpoints.
        /// </para>
        /// <para>
        /// Call it AFTER <c>AddServiceDefaults()</c>: the outbox-poll and probe filter processors
        /// service defaults register must run before this exporter's batch processor, or the spans
        /// they un-record are exported anyway. It can be active beside the vendor-neutral OTLP
        /// exporter and beside the other clouds' exporters; each ships its own copy.
        /// </para>
        /// </summary>
        /// <returns>The same builder instance for chaining.</returns>
        /// <exception cref="InvalidOperationException">
        /// <c>AddServiceDefaults()</c> (or <c>ConfigureOpenTelemetry()</c>) has not run yet, which is
        /// enforced whether or not the region is set; only one half of the key pair or of the log
        /// group and stream pair is set; or an endpoint override is not an absolute http or https URL.
        /// </exception>
        public TBuilder AddAwsTelemetryExporter()
        {
            if (!builder.Services.Any(descriptor =>
                    descriptor.ServiceType == typeof(HostIntegrationMarker)
                    && !descriptor.IsKeyedService
                    && descriptor.ImplementationInstance is HostIntegrationMarker marker
                    && string.Equals(marker.Name, HostIntegrationNames.ServiceDefaultsTelemetry, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "AddAwsTelemetryExporter() must run after AddServiceDefaults() (or ConfigureOpenTelemetry()): the outbox-poll and probe filter processors service defaults register have to run before the AWS exporter's batch processor, or the spans they drop are exported anyway. Move the call below builder.AddServiceDefaults().");
            }

            var settings = AwsTelemetrySettings.Read(builder.Configuration);

            // The gate. Absent means "do nothing": no exporter, no credential lookup and no receipt, so
            // a region that only appears later still trips the startup check.
            if (string.IsNullOrWhiteSpace(settings.Region))
            {
                return builder;
            }

            var regionName = settings.Region.Trim();
            var region = RegionEndpoint.GetBySystemName(regionName);
            var staticCredentials = AwsStaticCredentials.Resolve(
                settings.AccessKeyId,
                settings.SecretAccessKey,
                AwsTelemetrySettings.SectionPath);
            var exportLogs = ResolveLogDestination(settings);
            var tracesEndpoint = ResolveTracesEndpoint(builder.Configuration, regionName);
            var logsEndpoint = exportLogs ? ResolveLogsEndpoint(builder.Configuration, regionName) : null;

            // The receipt the core startup tripwire checks: AwsTelemetry:Region set without this call
            // fails the host instead of starting it with no telemetry export.
            builder.Services.AddSingleton(new HostIntegrationMarker(HostIntegrationNames.AwsTelemetryExporter));

            IAwsTelemetryCredentialSource credentials = staticCredentials is null
                ? new DefaultChainAwsTelemetryCredentialSource()
                : new StaticAwsTelemetryCredentialSource(staticCredentials);

            // The exporters are built by hand rather than through AddOtlpExporter: the OpenTelemetry SDK
            // refuses a signal-specific AddOtlpExporter on a service collection where UseOtlpExporter
            // (the vendor-neutral exporter service defaults enable) also runs, and this exporter has to
            // coexist with that one.
            var openTelemetry = builder.Services.AddOpenTelemetry()
                .ConfigureResource(resource => resource.AddAttributes(
                [
                    new("cloud.provider", "aws"),
                    new("cloud.region", regionName),
                ]))
                .WithTracing(tracing => tracing.AddProcessor(provider =>
                    new BatchActivityExportProcessor(new OtlpTraceExporter(
                        CreateExporterOptions(tracesEndpoint, TracesService, region, credentials, addedHeaders: null, provider)))));

            if (logsEndpoint is not null)
            {
                KeyValuePair<string, string>[] logHeaders =
                [
                    new(LogGroupHeader, settings.LogGroup!.Trim()),
                    new(LogStreamHeader, settings.LogStream!.Trim()),
                ];
                openTelemetry.WithLogging(logging => logging.AddProcessor(provider =>
                    new BatchLogRecordExportProcessor(new OtlpLogExporter(
                        CreateExporterOptions(logsEndpoint, LogsService, region, credentials, logHeaders, provider)))));
            }

            return builder;
        }
    }

    /// <summary>
    /// The traces URL: <c>AwsTelemetry:TracesEndpoint</c> as the full URL when set, else the regional
    /// X-Ray OTLP endpoint.
    /// </summary>
    /// <param name="configuration">The host configuration.</param>
    /// <param name="region">The region system name, for example <c>eu-west-1</c>.</param>
    /// <returns>The full traces URL.</returns>
    /// <exception cref="InvalidOperationException">The override is not an absolute http or https URL.</exception>
    internal static Uri ResolveTracesEndpoint(IConfiguration configuration, string region) =>
        ResolveEndpoint(configuration, nameof(AwsTelemetrySettings.TracesEndpoint), $"https://{TracesService}.{region}.amazonaws.com/v1/traces");

    /// <summary>
    /// The logs URL: <c>AwsTelemetry:LogsEndpoint</c> as the full URL when set, else the regional
    /// CloudWatch Logs OTLP endpoint.
    /// </summary>
    /// <param name="configuration">The host configuration.</param>
    /// <param name="region">The region system name, for example <c>eu-west-1</c>.</param>
    /// <returns>The full logs URL.</returns>
    /// <exception cref="InvalidOperationException">The override is not an absolute http or https URL.</exception>
    internal static Uri ResolveLogsEndpoint(IConfiguration configuration, string region) =>
        ResolveEndpoint(configuration, nameof(AwsTelemetrySettings.LogsEndpoint), $"https://{LogsService}.{region}.amazonaws.com/v1/logs");

    private static Uri ResolveEndpoint(IConfiguration configuration, string setting, string regionalDefault)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var key = AwsTelemetrySettings.Key(setting);
        var configured = configuration[key];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return new Uri(regionalDefault);
        }

        if (!Uri.TryCreate(configured.Trim(), UriKind.Absolute, out var endpoint) || !IsHttp(endpoint))
        {
            throw new InvalidOperationException(
                $"{key} must be the full absolute http or https URL of the signal endpoint, such as {regionalDefault}, but was \"{configured}\".");
        }

        return endpoint;
    }

    /// <summary>True when both the log group and stream are set, false when neither is.</summary>
    /// <exception cref="InvalidOperationException">Exactly one of the two is set.</exception>
    private static bool ResolveLogDestination(AwsTelemetrySettings settings)
    {
        var hasGroup = !string.IsNullOrWhiteSpace(settings.LogGroup);
        var hasStream = !string.IsNullOrWhiteSpace(settings.LogStream);
        if (hasGroup != hasStream)
        {
            throw new InvalidOperationException(
                $"{AwsTelemetrySettings.Key(nameof(AwsTelemetrySettings.LogGroup))} and {AwsTelemetrySettings.Key(nameof(AwsTelemetrySettings.LogStream))} must be set together. Set both to export logs to CloudWatch Logs, or neither to export traces only.");
        }

        return hasGroup;
    }

    private static bool IsHttp(Uri endpoint) =>
        string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
        || string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal);

    /// <summary>
    /// OTLP/HTTP protobuf options for one signal. The endpoint is the full URL, and the headers are
    /// cleared so an <c>OTEL_EXPORTER_OTLP_HEADERS</c> meant for the vendor-neutral exporter is never
    /// sent to AWS. Every request goes through the SigV4 signing handler.
    /// </summary>
    private static OtlpExporterOptions CreateExporterOptions(
        Uri endpoint,
        string service,
        RegionEndpoint region,
        IAwsTelemetryCredentialSource credentials,
        IEnumerable<KeyValuePair<string, string>>? addedHeaders,
        IServiceProvider provider)
    {
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger<AwsSigV4ExportHandler>();
        var timeProvider = provider.GetService<TimeProvider>() ?? TimeProvider.System;
        var options = new OtlpExporterOptions
        {
            Endpoint = endpoint,
            Protocol = OtlpExportProtocol.HttpProtobuf,
            Headers = null,
        };
        var timeout = TimeSpan.FromMilliseconds(options.TimeoutMilliseconds);
        options.HttpClientFactory = () => new HttpClient(
            new AwsSigV4ExportHandler(credentials, region, service, timeProvider, logger, addedHeaders)
            {
                InnerHandler = new HttpClientHandler(),
            })
        {
            Timeout = timeout,
        };
        return options;
    }
}

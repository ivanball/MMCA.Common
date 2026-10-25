using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Aspire.Integrations;
using MMCA.Common.Gcp.Telemetry;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace MMCA.Common.Gcp;

/// <summary>
/// Registration extension that exports traces and logs straight to the Google Cloud Telemetry API
/// (OTLP) beside the vendor-neutral OTLP exporter <c>AddServiceDefaults()</c> configures: the Google
/// Cloud twin of <c>AddAzureMonitorExporter()</c>, with no collector sidecar.
/// </summary>
public static class GoogleCloudTelemetryExporterExtensions
{
    /// <summary>The gate: the Google Cloud project the telemetry is written to and billed against.</summary>
    internal const string ProjectIdKey = "GoogleCloudTelemetry:ProjectId";

    /// <summary>The optional root URL override (a regional endpoint, or a local receiver).</summary>
    internal const string EndpointKey = "GoogleCloudTelemetry:Endpoint";

    /// <summary>The global Telemetry API endpoint.</summary>
    internal const string DefaultEndpoint = "https://telemetry.googleapis.com/";

    /// <summary>
    /// The resource attribute Google reads the project from. Google maps each OTLP resource to a
    /// monitored resource whose labels must include a project id, searched in a fixed order
    /// (<c>project_id</c>, <c>gcp.project.id</c>, <c>gcp.project_id</c>, ...); see "OTLP attributes
    /// to resource type mapping",
    /// https://docs.cloud.google.com/stackdriver/docs/reference/telemetry/otlp-attribute-to-logging-resource.
    /// </summary>
    internal const string ProjectIdAttribute = "gcp.project_id";

    extension<TBuilder>(TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        /// <summary>
        /// Exports traces and logs to the Google Cloud Telemetry API (<c>telemetry.googleapis.com</c>,
        /// OTLP over HTTP/protobuf) when <c>GoogleCloudTelemetry:ProjectId</c> is present, so they land
        /// in Cloud Trace and Cloud Logging. When absent or whitespace this method registers nothing,
        /// so local development and tests take no Google dependency at startup.
        /// <para>
        /// Configuration keys:
        /// <list type="bullet">
        ///   <item><c>GoogleCloudTelemetry:ProjectId</c> (the gate): the project written to. It is
        ///     sent as the <c>x-goog-user-project</c> quota project and stamped on the resource as
        ///     <c>gcp.project_id</c>, which is the attribute Google maps the data to a project
        ///     with.</item>
        ///   <item><c>GoogleCloudTelemetry:Endpoint</c> (optional): the root URL, default
        ///     <c>https://telemetry.googleapis.com</c>. Set a regional endpoint
        ///     (<c>https://telemetry.REGION.rep.googleapis.com</c>) to keep the data in a region, or a
        ///     local OTLP receiver in tests. <c>/v1/traces</c> and <c>/v1/logs</c> are appended.</item>
        /// </list>
        /// </para>
        /// <para>
        /// Authentication is Application Default Credentials (the attached service account on Cloud
        /// Run or GKE), loaded when the host starts, never at registration. The identity needs
        /// <c>roles/telemetry.writer</c> on the project and
        /// <c>roles/serviceusage.serviceUsageConsumer</c> on the quota project. A host without
        /// credentials still starts; the failure is logged and exports are refused locally until a
        /// token arrives.
        /// </para>
        /// <para>
        /// Resource mapping: Google picks the monitored resource from the resource attributes. With
        /// <c>service.name</c> and <c>service.instance.id</c> both set the data maps to
        /// <c>generic_task</c> (located by <c>cloud.region</c> or <c>cloud.availability_zone</c>);
        /// without them it falls back to <c>generic_node</c>. Supply them through
        /// <c>OTEL_SERVICE_NAME</c> and <c>OTEL_RESOURCE_ATTRIBUTES</c>; this call adds only
        /// <c>gcp.project_id</c>, which every exporter on the host then also carries.
        /// </para>
        /// <para>
        /// Metrics are NOT exported by this call. The Telemetry API accepts at most 200 data points per
        /// metric request, and the OpenTelemetry .NET SDK offers no supported way to split one metric's
        /// points across requests (a <c>Metric</c> cannot be constructed outside the SDK), so a host
        /// whose instruments carry more than 200 time series would have whole batches rejected. Ship
        /// metrics through the Google-built OpenTelemetry Collector instead: point the vendor-neutral
        /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> at a collector (a Cloud Run or GKE sidecar) whose batch
        /// processor caps each request at 200 points (<c>send_batch_max_size: 200</c>) before its
        /// <c>otlphttp</c> exporter, authenticated by the <c>googleclientauth</c> extension, sends it to
        /// the Telemetry API.
        /// </para>
        /// <para>
        /// Call it AFTER <c>AddServiceDefaults()</c>: the outbox-poll and probe filter processors
        /// service defaults register must run before this exporter's batch processor, or the spans
        /// they un-record are exported anyway. It can be active beside the vendor-neutral OTLP
        /// exporter and beside Azure Monitor; each ships its own copy.
        /// </para>
        /// </summary>
        /// <returns>The same builder instance for chaining.</returns>
        /// <exception cref="InvalidOperationException">
        /// <c>AddServiceDefaults()</c> (or <c>ConfigureOpenTelemetry()</c>) has not run yet, which is
        /// enforced whether or not the project is set, so a misordered host fails on a developer
        /// machine; or <c>GoogleCloudTelemetry:Endpoint</c> is not an absolute http or https URL.
        /// </exception>
        public TBuilder AddGoogleCloudTelemetryExporter()
        {
            if (!builder.Services.Any(descriptor =>
                    descriptor.ServiceType == typeof(HostIntegrationMarker)
                    && !descriptor.IsKeyedService
                    && descriptor.ImplementationInstance is HostIntegrationMarker marker
                    && string.Equals(marker.Name, HostIntegrationNames.ServiceDefaultsTelemetry, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "AddGoogleCloudTelemetryExporter() must run after AddServiceDefaults() (or ConfigureOpenTelemetry()): the outbox-poll and probe filter processors service defaults register have to run before the Google Cloud exporter's batch processor, or the spans they drop are exported anyway. Move the call below builder.AddServiceDefaults().");
            }

            var projectId = builder.Configuration[ProjectIdKey];

            // The gate. Absent means "do nothing": no exporter, no Google credential at startup and no
            // receipt, so a project id that only appears later still trips the startup check.
            if (string.IsNullOrWhiteSpace(projectId))
            {
                return builder;
            }

            projectId = projectId.Trim();
            var endpoint = ResolveEndpoint(builder.Configuration);

            // The receipt the core startup tripwire checks: GoogleCloudTelemetry:ProjectId set without
            // this call fails the host instead of starting it with no telemetry export.
            builder.Services.AddSingleton(new HostIntegrationMarker(HostIntegrationNames.GoogleCloudTelemetryExporter));

            builder.Services.TryAddSingleton<IGoogleCloudTelemetryTokenSource, ApplicationDefaultTelemetryTokenSource>();
            builder.Services.AddSingleton<GoogleCloudTelemetryAccessToken>();
            builder.Services.AddHostedService(provider => provider.GetRequiredService<GoogleCloudTelemetryAccessToken>());

            // The exporters are built by hand rather than through AddOtlpExporter: the OpenTelemetry SDK
            // refuses a signal-specific AddOtlpExporter on a service collection where UseOtlpExporter
            // (the vendor-neutral exporter service defaults enable) also runs, and this exporter has to
            // coexist with that one.
            builder.Services.AddOpenTelemetry()
                .ConfigureResource(resource => resource.AddAttributes([new(ProjectIdAttribute, projectId)]))
                .WithTracing(tracing => tracing.AddProcessor(provider =>
                    new BatchActivityExportProcessor(new OtlpTraceExporter(
                        CreateExporterOptions(SignalEndpoint(endpoint, "v1/traces"), projectId, provider)))))
                .WithLogging(logging => logging.AddProcessor(provider =>
                    new BatchLogRecordExportProcessor(new OtlpLogExporter(
                        CreateExporterOptions(SignalEndpoint(endpoint, "v1/logs"), projectId, provider)))));

            return builder;
        }
    }

    /// <summary>
    /// Reads <c>GoogleCloudTelemetry:Endpoint</c>, defaulting to the global endpoint, as a root URL
    /// ending in a slash.
    /// </summary>
    /// <param name="configuration">The host configuration.</param>
    /// <returns>The root URL the signal paths are appended to.</returns>
    /// <exception cref="InvalidOperationException">The value is not an absolute http or https URL.</exception>
    internal static Uri ResolveEndpoint(IConfiguration configuration)
    {
        var configured = configuration[EndpointKey];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return new Uri(DefaultEndpoint);
        }

        if (!Uri.TryCreate(configured.Trim(), UriKind.Absolute, out var endpoint) || !IsHttp(endpoint))
        {
            throw new InvalidOperationException(
                $"{EndpointKey} must be an absolute http or https URL such as {DefaultEndpoint} or https://telemetry.us-central1.rep.googleapis.com/, but was \"{configured}\".");
        }

        // A root without a trailing slash would have its last segment replaced by the signal path.
        return endpoint.AbsolutePath.EndsWith('/')
            ? endpoint
            : new UriBuilder(endpoint) { Path = $"{endpoint.AbsolutePath}/" }.Uri;
    }

    /// <summary>Appends an OTLP signal path (<c>v1/traces</c>, <c>v1/logs</c>) to the root URL.</summary>
    /// <param name="root">The root URL, ending in a slash.</param>
    /// <param name="signalPath">The relative signal path.</param>
    /// <returns>The full export URL.</returns>
    internal static Uri SignalEndpoint(Uri root, string signalPath) => new(root, signalPath);

    private static bool IsHttp(Uri endpoint) =>
        string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
        || string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal);

    /// <summary>
    /// OTLP/HTTP protobuf options for one signal. The endpoint is the full URL (an endpoint set in code
    /// is used as is, with no path appended), and the headers are cleared so an
    /// <c>OTEL_EXPORTER_OTLP_HEADERS</c> meant for the vendor-neutral exporter (an Aspire dashboard
    /// key, say) is never sent to Google.
    /// </summary>
    private static OtlpExporterOptions CreateExporterOptions(Uri endpoint, string projectId, IServiceProvider provider)
    {
        var accessToken = provider.GetRequiredService<GoogleCloudTelemetryAccessToken>();
        var options = new OtlpExporterOptions
        {
            Endpoint = endpoint,
            Protocol = OtlpExportProtocol.HttpProtobuf,
            Headers = null,
        };
        var timeout = TimeSpan.FromMilliseconds(options.TimeoutMilliseconds);
        options.HttpClientFactory = () => new HttpClient(
            new GoogleCloudTelemetryAuthHandler(accessToken, projectId) { InnerHandler = new HttpClientHandler() })
        {
            Timeout = timeout,
        };
        return options;
    }
}

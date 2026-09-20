using System.Globalization;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace MMCA.Common.Aspire;

public static partial class Extensions
{
    /// <summary>
    /// Configuration key of the probe-telemetry cost knob. Defaults to <see langword="true"/>; see
    /// <see cref="IsProbeTelemetryFilterEnabled"/>.
    /// </summary>
    internal const string FilterProbeTelemetryConfigKey = "Telemetry:FilterProbeTelemetry";

    /// <summary>
    /// Configuration key of the Polly duration-histogram cost knob. Defaults to <see langword="false"/>
    /// (the two histograms are dropped); see <see cref="IsInstrumentationEnabled"/>.
    /// </summary>
    internal const string EnablePollyDurationMetricsConfigKey = "Telemetry:EnablePollyDurationMetrics";

    /// <summary>
    /// The meter Polly v8 emits through (<c>Polly.Extensions</c>' telemetry listener). Named as a
    /// literal because Aspire holds no reference to Polly's own assemblies.
    /// </summary>
    internal const string PollyMeterName = "Polly";

    /// <summary>
    /// The one name the optional AI package publishes both its traces and its metrics under
    /// (<c>AiUsageMeter.MeterName</c>, which is also the source name it hands to
    /// <c>UseOpenTelemetry</c>), so a host enables the model dependency's telemetry with one string.
    /// <para>
    /// A literal, deliberately: <c>AiDependencyIsolationTestsBase</c> bars a project reference from
    /// this package to <c>MMCA.Common.AI</c>, which is what keeps the optional language-model
    /// dependency out of every host that never adopts it. Subscribing a meter and a source that
    /// nothing publishes to is inert, so a host without the AI package is unaffected.
    /// </para>
    /// </summary>
    internal const string AiTelemetryName = "MMCA.Common.AI";

    /// <summary>
    /// Polly's resilience-event counter: one increment per strategy event, tagged with
    /// <c>pipeline.name</c>, <c>strategy.name</c>, <c>event.name</c> (OnRetry / OnCircuitOpened /
    /// OnCircuitClosed / OnTimeout / ...), <c>event.severity</c> and <c>exception.type</c>. Always
    /// exported: it is the only production signal that a client is retrying or that a circuit opened.
    /// </summary>
    internal const string PollyStrategyEventsInstrument = "resilience.polly.strategy.events";

    /// <summary>Polly's per-attempt duration histogram; dropped unless a host opts in (rubric §31).</summary>
    internal const string PollyAttemptDurationInstrument = "resilience.polly.strategy.attempt.duration";

    /// <summary>Polly's whole-pipeline duration histogram; dropped unless a host opts in (rubric §31).</summary>
    internal const string PollyPipelineDurationInstrument = "resilience.polly.pipeline.duration";

    extension<TBuilder>(TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        /// <summary>
        /// Configures OpenTelemetry logging, metrics (ASP.NET Core, HttpClient, .NET runtime, the
        /// MMCA.Common meters and Polly's resilience meter), and distributed tracing. Exports are
        /// sent to the OTLP endpoint when the
        /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> environment variable is set (automatically
        /// provided by the Aspire dashboard).
        /// </summary>
        /// <returns>The same builder instance for chaining.</returns>
        public TBuilder ConfigureOpenTelemetry()
        {
            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;
            });

            builder.Services.AddOpenTelemetry()
                .WithMetrics(metrics => ConfigureMetrics(metrics, builder.Configuration))
                .WithTracing(tracing =>
                {
                    tracing.AddSource(builder.Environment.ApplicationName)
                        .AddSource("MMCA.Common.Outbox")
                        .AddSource("MMCA.Common.InternalCommands")
                        .AddSource(AiTelemetryName);

                    // Cost control (rubric §31): health-probe traces. Container Apps liveness and
                    // readiness probes, the gateway's downstream aggregate probes, YARP active
                    // health checks and the availability web test made up 100% of the AppRequests
                    // rows in both production workspaces, and their children (the health check's
                    // SQL SELECT 1, the Redis PING, the gateway's HttpClient calls to each
                    // backend's /alive) most of the AppDependencies rows. None of it carries
                    // end-user signal and none of it is touched by Telemetry:TracesSampleRatio,
                    // because probe spans are what the sampler is asked to keep proportionally.
                    // On by default (this is chatter no host wants billed); a host that is
                    // debugging its own probes sets Telemetry:FilterProbeTelemetry=false.
                    // Metrics are deliberately untouched: http.server.request.duration, Kestrel
                    // and routing instruments keep flowing, so probe traffic stays on dashboards.
                    var filterProbeTelemetry = IsProbeTelemetryFilterEnabled(builder.Configuration);
                    if (filterProbeTelemetry)
                    {
                        // Both filters configure the DEFAULT-named instrumentation options, so they
                        // also apply to the instrumentation the Azure Monitor distro adds: unlike
                        // the metrics toggles above, these are authoritative without a View.
                        tracing.AddAspNetCoreInstrumentation(options =>
                                options.Filter = Telemetry.ProbeTelemetryFilter.ShouldCollectRequest)
                            .AddHttpClientInstrumentation(options =>
                                options.FilterHttpRequestMessage = Telemetry.ProbeTelemetryFilter.ShouldCollectOutgoing);
                    }
                    else
                    {
                        tracing.AddAspNetCoreInstrumentation()
                            .AddHttpClientInstrumentation();
                    }

                    // Drops recurring outbox poll spans (and their SqlClient children from the
                    // Azure Monitor distro) from export — idle polling would otherwise dominate
                    // telemetry ingestion cost. Must be registered here, before
                    // AddOpenTelemetryExporters() below, so its OnEnd clears the Recorded flag
                    // before the exporters' batch processors check it.
                    tracing.AddProcessor(new Telemetry.OutboxPollFilterProcessor());

                    if (filterProbeTelemetry)
                    {
                        // Same registration-order requirement, same reason: the inbound filter only
                        // refuses the probe request span itself, while its dependency children are
                        // sampled independently and need un-recording before the exporters look.
                        tracing.AddProcessor(new Telemetry.ProbeTelemetryFilterProcessor());
                    }

                    // Cost control (rubric §31): head-based trace sampling. Unset by default, so a
                    // host samples everything (no behavior change). A deployed host sets
                    // Telemetry:TracesSampleRatio in (0,1) — e.g. 0.1 to keep 10% of traces — to cut
                    // trace-ingestion cost, the largest observability line item. ParentBased so a
                    // sampled-in request keeps its whole trace intact across service boundaries.
                    if (TryGetTraceSampleRatio(builder.Configuration, out var traceSampleRatio))
                        tracing.SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(traceSampleRatio)));
                });

            builder.AddOpenTelemetryExporters();

            return builder;
        }

        /// <summary>
        /// Conditionally enables telemetry exporters based on the environment:
        /// <list type="bullet">
        ///   <item>OTLP when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is present (the Aspire
        ///     dashboard sets this automatically; standalone deployments must supply it
        ///     explicitly).</item>
        ///   <item>Azure Monitor (Application Insights) when
        ///     <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> is present — set by the cloud
        ///     deployment (e.g., Container Apps Bicep) so logs, metrics, and traces flow to
        ///     the workspace-based Application Insights resource.</item>
        /// </list>
        /// Both can be active simultaneously; each exporter only ships its own copy.
        /// </summary>
        private void AddOpenTelemetryExporters()
        {
            var useOtlpExporter = !string.IsNullOrWhiteSpace(
                builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

            if (useOtlpExporter)
            {
                builder.Services.AddOpenTelemetry().UseOtlpExporter();
            }

            var useAzureMonitor = !string.IsNullOrWhiteSpace(
                builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]);

            if (useAzureMonitor)
            {
                builder.Services.AddOpenTelemetry().UseAzureMonitor();
            }
        }
    }

    /// <summary>
    /// Reads the optional <c>Telemetry:TracesSampleRatio</c> knob (rubric §31). Returns true with a
    /// ratio in the open interval (0,1) when a host opts into head-based trace sampling; returns false
    /// (sample everything, the default) when the key is absent, unparseable, or outside (0,1) — so a
    /// typo can never silently drop all telemetry.
    /// </summary>
    internal static bool TryGetTraceSampleRatio(IConfiguration configuration, out double ratio)
    {
        ratio = 1.0;
        var raw = configuration["Telemetry:TracesSampleRatio"];
        if (string.IsNullOrWhiteSpace(raw)
            || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || parsed is <= 0.0 or >= 1.0)
        {
            return false;
        }

        ratio = parsed;
        return true;
    }

    /// <summary>
    /// Reads an optional boolean metrics cost knob (rubric §31) at the given configuration key, e.g.
    /// <c>Telemetry:DisableRuntimeMetrics</c> or <c>Telemetry:DisableHttpClientMetrics</c>. Returns true
    /// (drop that instrumentation) only when the value parses as boolean <see langword="true"/>; absent, blank, or
    /// unparseable falls back to false (keep the instrumentation) so a typo can never silently blind a
    /// whole metric family. These two families are the dominant AppMetrics ingestion volume on a
    /// low-traffic multi-service deployment and carry no end-user-visible signal.
    /// </summary>
    internal static bool IsInstrumentationDisabled(IConfiguration configuration, string configKey)
        => bool.TryParse(configuration[configKey], out var disabled) && disabled;

    /// <summary>
    /// Reads an optional boolean opt-IN metrics knob (rubric §31), e.g.
    /// <c>Telemetry:EnablePollyDurationMetrics</c>. The mirror image of
    /// <see cref="IsInstrumentationDisabled"/>: the instrumentation is off by default and only a
    /// parseable boolean <see langword="true"/> turns it on, so absent, blank or unparseable all mean
    /// "stay off" and a typo can never silently add a high-volume stream to a host's bill.
    /// </summary>
    /// <param name="configuration">Configuration carrying the knob.</param>
    /// <param name="configKey">The knob's configuration key.</param>
    /// <returns><see langword="true"/> when the host opted the instrumentation in.</returns>
    internal static bool IsInstrumentationEnabled(IConfiguration configuration, string configKey)
        => bool.TryParse(configuration[configKey], out var enabled) && enabled;

    /// <summary>
    /// Reads the <c>Telemetry:FilterProbeTelemetry</c> cost knob (rubric §31), which keeps health-probe
    /// requests and their dependency children out of trace export. Unlike the metrics knobs this one
    /// defaults to <see langword="true"/>: probe chatter is ingestion no host wants billed, so absent,
    /// blank or unparseable all mean "filter", and only an explicit boolean <see langword="false"/>
    /// turns the filtering off (for a host debugging its own probes).
    /// </summary>
    /// <param name="configuration">Configuration carrying the knob.</param>
    /// <returns><see langword="true"/> when probe telemetry must be filtered.</returns>
    internal static bool IsProbeTelemetryFilterEnabled(IConfiguration configuration)
        => !bool.TryParse(configuration[FilterProbeTelemetryConfigKey], out var enabled) || enabled;

    /// <summary>
    /// The metrics half of <c>ConfigureOpenTelemetry</c>: instrumentation, the MMCA.Common meters,
    /// Polly's resilience meter, and the rubric §31 cost knobs that decide which of those streams
    /// actually reach an exporter. A method rather than an inline lambda so each knob stays readable.
    /// </summary>
    /// <param name="metrics">The meter provider being configured.</param>
    /// <param name="configuration">Configuration carrying the cost knobs.</param>
    private static void ConfigureMetrics(MeterProviderBuilder metrics, IConfiguration configuration)
    {
        metrics.AddAspNetCoreInstrumentation();

        // Cost control (rubric §31): HttpClient connection/request metrics
        // (http.client.open_connections / active_requests / request.duration) are the single
        // highest-volume AppMetrics contributor on a low-traffic multi-service deployment — the
        // pooled gRPC / service-discovery channels emit a high-frequency connection-gauge stream.
        // A deployed host sets Telemetry:DisableHttpClientMetrics=true to drop them; outbound
        // dependency latency is still captured as AppDependencies traces. Unset (default) keeps
        // them, so no behavior change for a host that does not opt in.
        if (IsInstrumentationDisabled(configuration, "Telemetry:DisableHttpClientMetrics"))
        {
            // Skipping AddHttpClientInstrumentation is NOT enough on its own. A deployed
            // host also calls UseAzureMonitor() (see AddOpenTelemetryExporters below), and
            // the Azure Monitor distro adds the System.Net.Http meter itself, so
            // http.client.open_connections kept flowing and stayed the single largest
            // AppMetrics stream in both production workspaces despite the toggle being on.
            // A View applies to the whole MeterProvider regardless of which component added
            // the meter, so dropping every instrument on those two meters makes the toggle
            // authoritative instead of advisory. System.Net.NameResolution rides along
            // because DNS-lookup metrics are part of the same HttpClient family and carry
            // no signal without it.
            metrics.AddView(instrument =>
                string.Equals(instrument.Meter.Name, "System.Net.Http", StringComparison.Ordinal)
                || string.Equals(instrument.Meter.Name, "System.Net.NameResolution", StringComparison.Ordinal)
                    ? MetricStreamConfiguration.Drop
                    : null);
        }
        else
        {
            metrics.AddHttpClientInstrumentation();
        }

        // Cost control (rubric §31): .NET runtime metrics (dotnet.gc.* / jit.* / thread_pool.* —
        // ~17 instruments emitted every collection interval regardless of traffic) are the second
        // highest-volume contributor and are rarely consulted operationally for these apps. A
        // deployed host sets Telemetry:DisableRuntimeMetrics=true to drop them. Unset keeps them.
        if (IsInstrumentationDisabled(configuration, "Telemetry:DisableRuntimeMetrics"))
        {
            // Same reasoning as the HttpClient branch above: the Azure Monitor distro adds
            // the System.Runtime meter on its own, so the toggle only becomes authoritative
            // once a View drops the whole meter.
            metrics.AddView(instrument =>
                string.Equals(instrument.Meter.Name, "System.Runtime", StringComparison.Ordinal)
                    ? MetricStreamConfiguration.Drop
                    : null);
        }
        else
        {
            metrics.AddRuntimeInstrumentation();
        }

        // MMCA.Common meters (literal names, because Aspire has no reference to the
        // defining assemblies): outbox counters and dispatch lag, CQRS RED histograms
        // plus query cache hit/miss, the idempotency filter's replay, conflict and
        // degraded counters, the recurring scheduler's run outcomes, duration and
        // schedule lag (inert in a host that never enables Scheduler:Enabled), the
        // broker transport's consumer faults plus outbox circuit-breaker openings
        // (inert in a host that stays on the in-process bus), the output-cache
        // eviction consumer's failed tag evictions, the swallowed failures of
        // best-effort side effects (both inert until a host opts into them), and the
        // optional AI package's token spend plus call duration (inert until a host
        // adds that package and enables Ai:Enabled).
        metrics.AddMeter("MMCA.Common.Outbox")
            .AddMeter("MMCA.Common.Cqrs")
            .AddMeter("MMCA.Common.Idempotency")
            .AddMeter("MMCA.Common.Scheduler")
            .AddMeter("MMCA.Common.Broker")
            .AddMeter("MMCA.Common.OutputCache")
            .AddMeter("MMCA.Common.BestEffort")
            .AddMeter("MMCA.Common.InternalCommands")
            .AddMeter(AiTelemetryName);

        // Polly's own meter (ADR-009). The standard resilience handler above is on every
        // HttpClient and every gRPC typed client, so it is the component that decides
        // whether an inter-service call is retried, timed out or refused by an open
        // circuit, and until this meter was subscribed none of that left the process.
        // A brownout looked, from the outside, exactly like latency.
        metrics.AddMeter(PollyMeterName);

        // Cost control (rubric §31): Polly's two duration HISTOGRAMS
        // (resilience.polly.strategy.attempt.duration, resilience.polly.pipeline.duration)
        // are per-bucket streams on a pipeline that runs on every outbound call, and they
        // re-measure what http.client.request.duration and the dependency traces already
        // report. They stay off unless a host sets Telemetry:EnablePollyDurationMetrics=true
        // (while debugging a retry storm, say). The resilience.polly.strategy.events COUNTER
        // is never dropped: OnRetry / OnCircuitOpened / OnCircuitClosed / OnTimeout is the
        // signal the whole meter exists for, and it is low cardinality and low volume.
        if (!IsInstrumentationEnabled(configuration, EnablePollyDurationMetricsConfigKey))
        {
            metrics.AddView(instrument =>
                string.Equals(instrument.Meter.Name, PollyMeterName, StringComparison.Ordinal)
                && (string.Equals(instrument.Name, PollyAttemptDurationInstrument, StringComparison.Ordinal)
                    || string.Equals(instrument.Name, PollyPipelineDurationInstrument, StringComparison.Ordinal))
                    ? MetricStreamConfiguration.Drop
                    : null);
        }
    }
}

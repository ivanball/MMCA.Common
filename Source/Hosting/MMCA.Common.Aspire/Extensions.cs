using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMCA.Common.Aspire.Warmup;
using MMCA.Common.Shared.Resilience;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace MMCA.Common.Aspire;

/// <summary>
/// Shared service defaults applied to every project in the Aspire application model.
/// Configures OpenTelemetry, health checks, service discovery, and HTTP resilience
/// policies so that individual projects do not need to repeat this setup.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with multiple extension(T) blocks in one static class, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class Extensions
{
    extension<TBuilder>(TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        /// <summary>
        /// Registers all service defaults: OpenTelemetry, health checks, service discovery,
        /// and HTTP client resilience (Polly). Call this early in <c>Program.cs</c> before
        /// adding module-specific services.
        /// </summary>
        /// <returns>The same builder instance for chaining.</returns>
        public TBuilder AddServiceDefaults()
        {
            builder.ConfigureOpenTelemetry();
            builder.AddDefaultHealthChecks();
            builder.AddWarmupReadiness();
            builder.Services.AddServiceDiscovery();

            // HttpClient defaults applied via ConfigureHttpClientDefaults — affects every
            // HttpClient registered downstream (typed clients, named clients, YARP forwarder).
            builder.Services.ConfigureHttpClientDefaults(http =>
            {
                // Polly resilience pipeline (values from HttpResilienceDefaults, shared with the
                // gRPC typed clients in MMCA.Common.Grpc):
                //   - 30s per-attempt timeout
                //   - Circuit breaker sampled over 60s
                //   - 90s total request timeout (including retries)
                //   - ONE retry per hop: the UI service base classes own user-facing retries;
                //     stacking full retry budgets at every hop multiplies load during a backend
                //     brownout (previously up to 4 outer x 4 inner = 16 gateway hits per action).
                http.AddStandardResilienceHandler(options =>
                {
                    options.AttemptTimeout.Timeout = HttpResilienceDefaults.AttemptTimeout;
                    options.CircuitBreaker.SamplingDuration = HttpResilienceDefaults.CircuitBreakerSamplingDuration;
                    options.TotalRequestTimeout.Timeout = HttpResilienceDefaults.TotalRequestTimeout;
                    options.Retry.MaxRetryAttempts = HttpResilienceDefaults.MaxRetryAttempts;
                });
                http.AddServiceDiscovery();

                // SocketsHttpHandler tuned to survive long idle periods on ACA Consumption plan:
                //   - PooledConnectionLifetime: recycle connections every 10 min so DNS changes
                //     (e.g., ACA replica rollover) are picked up without app restart.
                //   - PooledConnectionIdleTimeout: keep idle connections in the pool for 5 min so
                //     low-traffic inter-service calls don't pay TCP+TLS handshake every time.
                //   - KeepAlivePingDelay/Timeout: socket-level keep-alive pings every 60s keep the
                //     TCP connection alive without generating HTTP traffic — crucially, this does
                //     NOT count as user traffic to the ACA platform, so the replica stays on
                //     idle-vCPU billing (~8x cheaper than active).
                //   - EnableMultipleHttp2Connections: HTTP/2 connections are multiplexed but a
                //     single connection can become a bottleneck; allow new ones when needed.
                http.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    PooledConnectionLifetime = HttpResilienceDefaults.PooledConnectionLifetime,
                    PooledConnectionIdleTimeout = HttpResilienceDefaults.PooledConnectionIdleTimeout,
                    KeepAlivePingDelay = HttpResilienceDefaults.KeepAlivePingDelay,
                    KeepAlivePingTimeout = HttpResilienceDefaults.KeepAlivePingTimeout,
                    KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
                    EnableMultipleHttp2Connections = true,
                });
            });

            return builder;
        }

        /// <summary>
        /// Registers the warm-up infrastructure: a singleton <see cref="WarmupReadinessGate"/>,
        /// the <see cref="WarmupHostedService"/> background runner, the
        /// <see cref="WarmupReadinessHealthCheck"/> tagged <c>ready</c>, and the built-in
        /// <see cref="OpenIdConnectMetadataWarmupTask"/> that pre-fetches the OIDC discovery
        /// document for every configured JwtBearer scheme. Additional tasks can be registered
        /// with <c>AddWarmupTask&lt;T&gt;()</c>.
        /// </summary>
        /// <returns>The same builder instance for chaining.</returns>
        public TBuilder AddWarmupReadiness()
        {
            builder.Services.AddSingleton<WarmupReadinessGate>();
            builder.Services.AddHostedService<WarmupHostedService>();

            builder.Services.AddSingleton<IWarmupTask, OpenIdConnectMetadataWarmupTask>();

            builder.Services.AddHealthChecks()
                .AddCheck<WarmupReadinessHealthCheck>("warmup", tags: ["ready"]);

            return builder;
        }

        /// <summary>
        /// Configures OpenTelemetry logging, metrics (ASP.NET Core, HttpClient, .NET runtime),
        /// and distributed tracing. Exports are sent to the OTLP endpoint when the
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
                .WithMetrics(metrics =>
                {
                    metrics.AddAspNetCoreInstrumentation();

                    // Cost control (rubric §31): HttpClient connection/request metrics
                    // (http.client.open_connections / active_requests / request.duration) are the single
                    // highest-volume AppMetrics contributor on a low-traffic multi-service deployment — the
                    // pooled gRPC / service-discovery channels emit a high-frequency connection-gauge stream.
                    // A deployed host sets Telemetry:DisableHttpClientMetrics=true to drop them; outbound
                    // dependency latency is still captured as AppDependencies traces. Unset (default) keeps
                    // them, so no behavior change for a host that does not opt in.
                    if (!IsInstrumentationDisabled(builder.Configuration, "Telemetry:DisableHttpClientMetrics"))
                    {
                        metrics.AddHttpClientInstrumentation();
                    }

                    // Cost control (rubric §31): .NET runtime metrics (dotnet.gc.* / jit.* / thread_pool.* —
                    // ~17 instruments emitted every collection interval regardless of traffic) are the second
                    // highest-volume contributor and are rarely consulted operationally for these apps. A
                    // deployed host sets Telemetry:DisableRuntimeMetrics=true to drop them. Unset keeps them.
                    if (!IsInstrumentationDisabled(builder.Configuration, "Telemetry:DisableRuntimeMetrics"))
                    {
                        metrics.AddRuntimeInstrumentation();
                    }

                    // MMCA.Common meters (literal names — Aspire has no reference to the defining
                    // assemblies): outbox dead-letter counter and CQRS RED histograms.
                    metrics.AddMeter("MMCA.Common.Outbox")
                        .AddMeter("MMCA.Common.Cqrs");
                })
                .WithTracing(tracing =>
                {
                    tracing.AddSource(builder.Environment.ApplicationName)
                        .AddSource("MMCA.Common.Outbox")
                        .AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()

                        // Drops recurring outbox poll spans (and their SqlClient children from the
                        // Azure Monitor distro) from export — idle polling would otherwise dominate
                        // telemetry ingestion cost. Must be registered here, before
                        // AddOpenTelemetryExporters() below, so its OnEnd clears the Recorded flag
                        // before the exporters' batch processors check it.
                        .AddProcessor(new Telemetry.OutboxPollFilterProcessor());

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
        /// Adds a baseline "self" health check tagged with "live". The /alive endpoint filters
        /// to this tag for Kubernetes-style liveness probes, while /health requires all checks
        /// (including module and database checks added elsewhere) to pass.
        /// </summary>
        /// <returns>The same builder instance for chaining.</returns>
        public TBuilder AddDefaultHealthChecks()
        {
            builder.Services.AddHealthChecks()
                .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

            return builder;
        }

        /// <summary>
        /// Conditionally registers health checks for infrastructure dependencies (Redis, RabbitMQ)
        /// when their connection strings are configured. These are tagged as readiness checks only —
        /// they appear in <c>/health</c> but not <c>/alive</c>, so a transient infrastructure outage
        /// does not kill the process.
        /// </summary>
        /// <returns>The same builder instance for chaining.</returns>
        public TBuilder AddInfrastructureHealthChecks()
        {
            var healthChecks = builder.Services.AddHealthChecks();

            var redisConnectionString = builder.Configuration.GetConnectionString("redis");
            if (!string.IsNullOrWhiteSpace(redisConnectionString))
            {
                healthChecks.AddRedis(redisConnectionString, name: "redis");
            }

            var rabbitConnectionString = builder.Configuration.GetConnectionString("rabbitmq")
                ?? builder.Configuration.GetConnectionString("messaging");
            if (!string.IsNullOrWhiteSpace(rabbitConnectionString)
                && Uri.TryCreate(rabbitConnectionString, UriKind.Absolute, out var rabbitUri))
            {
                healthChecks.AddRabbitMQ(async _ =>
                {
                    var factory = new RabbitMQ.Client.ConnectionFactory { Uri = rabbitUri };
                    return await factory.CreateConnectionAsync().ConfigureAwait(false);
                }, name: "rabbitmq");
            }

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

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers a custom <see cref="IWarmupTask"/> implementation that will run once at host
        /// startup alongside the built-in tasks. Use for service-specific pre-fetches (output cache,
        /// reference data, etc.).
        /// </summary>
        /// <typeparam name="TTask">The warm-up task implementation type.</typeparam>
        /// <returns>The same service collection for chaining.</returns>
        public IServiceCollection AddWarmupTask<TTask>()
            where TTask : class, IWarmupTask
        {
            services.AddSingleton<IWarmupTask, TTask>();
            return services;
        }
    }

    extension(WebApplication app)
    {
        /// <summary>
        /// Maps the standard health-check endpoints:
        /// <list type="bullet">
        ///   <item><c>/health</c> — all checks must pass (used by humans/dashboards).</item>
        ///   <item><c>/alive</c> — liveness probe; only "live"-tagged checks must pass.</item>
        ///   <item><c>/health/ready</c> — readiness probe; everything except "live"-only checks.
        ///     Reports unhealthy until the <see cref="WarmupReadinessGate"/> opens, so ACA
        ///     ingress holds back traffic from a replica that is still warming up.</item>
        /// </list>
        /// </summary>
        /// <returns>The same application instance for chaining.</returns>
        public WebApplication MapDefaultEndpoints()
        {
            app.MapHealthChecks("/health");

            // Liveness: only the "self" check (tagged "live") — avoids marking the
            // process as dead when an external dependency (e.g., SQL Server) is down.
            app.MapHealthChecks("/alive", new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            });

            // Readiness: everything except "live"-only checks. Warm-up gate is tagged "ready"
            // and reports unhealthy until WarmupHostedService finishes; untagged dependency
            // checks (e.g., AddSqlServer) also show up here so a failing dependency removes
            // the replica from traffic without restarting the container.
            app.MapHealthChecks("/health/ready", new HealthCheckOptions
            {
                Predicate = r => !r.Tags.Contains("live")
            });

            return app;
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
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMCA.Common.Aspire.Warmup;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace MMCA.Common.Aspire;

/// <summary>
/// Shared service defaults applied to every project in the Aspire application model.
/// Configures OpenTelemetry, health checks, service discovery, and HTTP resilience
/// policies so that individual projects do not need to repeat this setup.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// Registers all service defaults: OpenTelemetry, health checks, service discovery,
    /// and HTTP client resilience (Polly). Call this early in <c>Program.cs</c> before
    /// adding module-specific services.
    /// </summary>
    /// <typeparam name="TBuilder">The host application builder type.</typeparam>
    /// <param name="builder">The host application builder to configure.</param>
    /// <returns>The same builder instance for chaining.</returns>
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();
        builder.AddWarmupReadiness();
        builder.Services.AddServiceDiscovery();

        // HttpClient defaults applied via ConfigureHttpClientDefaults — affects every
        // HttpClient registered downstream (typed clients, named clients, YARP forwarder).
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Polly resilience pipeline:
            //   - 30s per-attempt timeout
            //   - Circuit breaker sampled over 60s
            //   - 90s total request timeout (including retries)
            http.AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(90);
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
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
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
    /// <typeparam name="TBuilder">The host application builder type.</typeparam>
    /// <param name="builder">The host application builder to configure.</param>
    /// <returns>The same builder instance for chaining.</returns>
    public static TBuilder AddWarmupReadiness<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddSingleton<WarmupReadinessGate>();
        builder.Services.AddHostedService<WarmupHostedService>();

        builder.Services.AddSingleton<IWarmupTask, OpenIdConnectMetadataWarmupTask>();

        builder.Services.AddHealthChecks()
            .AddCheck<WarmupReadinessHealthCheck>("warmup", tags: ["ready"]);

        return builder;
    }

    /// <summary>
    /// Registers a custom <see cref="IWarmupTask"/> implementation that will run once at host
    /// startup alongside the built-in tasks. Use for service-specific pre-fetches (output cache,
    /// reference data, etc.).
    /// </summary>
    /// <typeparam name="TTask">The warm-up task implementation type.</typeparam>
    /// <param name="services">The service collection to register the task in.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddWarmupTask<TTask>(this IServiceCollection services)
        where TTask : class, IWarmupTask
    {
        services.AddSingleton<IWarmupTask, TTask>();
        return services;
    }

    /// <summary>
    /// Configures OpenTelemetry logging, metrics (ASP.NET Core, HttpClient, .NET runtime),
    /// and distributed tracing. Exports are sent to the OTLP endpoint when the
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> environment variable is set (automatically
    /// provided by the Aspire dashboard).
    /// </summary>
    /// <typeparam name="TBuilder">The host application builder type.</typeparam>
    /// <param name="builder">The host application builder to configure.</param>
    /// <returns>The same builder instance for chaining.</returns>
    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation())
            .WithTracing(tracing =>
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddSource("MMCA.Common.Outbox")
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation());

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    /// <summary>
    /// Adds a baseline "self" health check tagged with "live". The /alive endpoint filters
    /// to this tag for Kubernetes-style liveness probes, while /health requires all checks
    /// (including module and database checks added elsewhere) to pass.
    /// </summary>
    /// <typeparam name="TBuilder">The host application builder type.</typeparam>
    /// <param name="builder">The host application builder to configure.</param>
    /// <returns>The same builder instance for chaining.</returns>
    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
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
    /// <typeparam name="TBuilder">The host application builder type.</typeparam>
    /// <param name="builder">The host application builder to configure.</param>
    /// <returns>The same builder instance for chaining.</returns>
    public static TBuilder AddInfrastructureHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
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
    /// Maps the standard health-check endpoints:
    /// <list type="bullet">
    ///   <item><c>/health</c> — all checks must pass (used by humans/dashboards).</item>
    ///   <item><c>/alive</c> — liveness probe; only "live"-tagged checks must pass.</item>
    ///   <item><c>/health/ready</c> — readiness probe; everything except "live"-only checks.
    ///     Reports unhealthy until the <see cref="WarmupReadinessGate"/> opens, so ACA
    ///     ingress holds back traffic from a replica that is still warming up.</item>
    /// </list>
    /// </summary>
    /// <param name="app">The web application to configure.</param>
    /// <returns>The same application instance for chaining.</returns>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
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

    /// <summary>
    /// Conditionally enables the OTLP exporter when the standard
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> environment variable is present. The Aspire
    /// dashboard sets this automatically; standalone deployments must supply it explicitly.
    /// </summary>
    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(
            builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }
}

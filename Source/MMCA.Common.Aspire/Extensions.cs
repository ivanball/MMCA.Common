using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
        builder.Services.AddServiceDiscovery();

        // Polly resilience pipeline applied to all HttpClient instances:
        //   - 30s per-attempt timeout
        //   - Circuit breaker sampled over 60s
        //   - 90s total request timeout (including retries)
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(90);
            });
            http.AddServiceDiscovery();
        });

        return builder;
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
    /// Maps the standard health-check endpoints:
    /// <list type="bullet">
    ///   <item><c>/health</c> — readiness probe; all checks must pass.</item>
    ///   <item><c>/alive</c> — liveness probe; only "live"-tagged checks must pass.</item>
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

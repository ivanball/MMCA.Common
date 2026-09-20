using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Aspire.Warmup;
using MMCA.Common.Shared.Resilience;

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
public static partial class Extensions
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
                //   - ONE retry per hop: the UI service base classes own user-facing retries.
                //     Stacking full retry budgets at every hop multiplies load during a backend
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
                .AddCheck<WarmupReadinessHealthCheck>("warmup", tags: [HealthCheckTags.Ready]);

            return builder;
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
}

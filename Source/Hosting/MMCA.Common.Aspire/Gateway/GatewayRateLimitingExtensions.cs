using System.Diagnostics.CodeAnalysis;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.Aspire.Gateway;

/// <summary>
/// Edge rate limiting for a reverse-proxy gateway: a per-client-IP fixed window chained with a
/// process-wide concurrency ceiling, both rejecting with <c>429</c>.
/// <para>
/// Deliberately different from the service-side <c>AddCommonRateLimiting</c> in MMCA.Common.API,
/// which partitions by authenticated user and exempts anonymous traffic. At the edge there is no
/// authenticated identity yet (the gateway forwards the token, it does not validate it into a
/// principal), and anonymous traffic is precisely what has to be bounded, so the partition key is
/// the client IP and anonymity is not an exemption.
/// </para>
/// <para>
/// The two limiters are chained rather than merged because they answer different questions: the
/// fixed window bounds how fast ONE caller may arrive, the concurrency limiter bounds how much work
/// the replica will hold at once regardless of who sent it. A request must satisfy both.
/// </para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with multiple extension(T) blocks in one static class, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class GatewayRateLimitingExtensions
{
    /// <summary>Partition key used for every request the limiters let through untouched.</summary>
    private const string BypassPartitionKey = "__bypass";

    /// <summary>Partition key used when the client IP cannot be resolved (fail open).</summary>
    private const string UnknownIpPartitionKey = "__unknown-ip";

    /// <summary>Single partition key for the process-wide concurrency ceiling.</summary>
    private const string ConcurrencyPartitionKey = "__gateway";

    /// <summary>
    /// Path prefixes that are exempt whatever the configuration says: liveness/readiness probes and
    /// JWKS discovery run at high frequency by design, and throttling them converts a traffic spike
    /// into a failed probe and a container restart.
    /// </summary>
    private static readonly string[] AlwaysBypassedPrefixes = ["/health", "/alive", "/.well-known"];

    /// <summary>
    /// Whether this request is exempt from BOTH edge limiters: an always-bypassed infrastructure
    /// prefix, or one of the host's configured <see cref="GatewayRateLimitingSettings.BypassPathPrefixes"/>.
    /// Matching is on whole path segments (so <c>/healthz</c> is NOT matched by <c>/health</c>) and
    /// case-insensitive.
    /// </summary>
    /// <param name="path">The request path.</param>
    /// <param name="settings">The bound gateway settings.</param>
    /// <returns><see langword="true"/> when the request must not be limited.</returns>
    /// <remarks>Internal (not private) so the matcher is unit-testable via <c>InternalsVisibleTo</c>.</remarks>
    internal static bool IsBypassed(PathString path, GatewayRateLimitingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var probe = path;
        return AlwaysBypassedPrefixes
            .Concat(settings.BypassPathPrefixes)
            .Any(prefix => !string.IsNullOrWhiteSpace(prefix)
                && probe.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Per-client-IP partition: no limiter for bypassed paths, no limiter for an unresolvable IP
    /// (fail open), otherwise one fixed window per IP.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="settings">The bound gateway settings.</param>
    /// <returns>The partition this request counts against.</returns>
    /// <remarks>Internal (not private) so the partition-key selection is unit-testable.</remarks>
    internal static RateLimitPartition<string> ClientIpPartition(
        HttpContext httpContext,
        GatewayRateLimitingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(settings);

        if (IsBypassed(httpContext.Request.Path, settings))
        {
            return RateLimitPartition.GetNoLimiter(BypassPartitionKey);
        }

        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString();
        if (clientIp is null)
        {
            // Fail open rather than collapsing every unattributable request into one shared bucket,
            // which would throttle an in-process TestServer and the integration tier to a standstill.
            return RateLimitPartition.GetNoLimiter(UnknownIpPartitionKey);
        }

        return RateLimitPartition.GetFixedWindowLimiter(clientIp, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = settings.PermitLimit,
            Window = TimeSpan.FromSeconds(settings.WindowSeconds),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });
    }

    /// <summary>
    /// Process-wide concurrency partition: one bucket for the whole replica, bypassed on the same
    /// paths the per-IP window is.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="settings">The bound gateway settings.</param>
    /// <returns>The partition this request counts against.</returns>
    /// <remarks>Internal (not private) so the partition-key selection is unit-testable.</remarks>
    internal static RateLimitPartition<string> ConcurrencyPartition(
        HttpContext httpContext,
        GatewayRateLimitingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(settings);

        return IsBypassed(httpContext.Request.Path, settings)
            ? RateLimitPartition.GetNoLimiter(BypassPartitionKey)
            : RateLimitPartition.GetConcurrencyLimiter(ConcurrencyPartitionKey, _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = settings.GlobalConcurrencyLimit,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            });
    }

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the edge rate limiter from the <c>GatewayRateLimiting</c> configuration section
        /// (see <see cref="GatewayRateLimitingSettings"/> for the per-replica trade-off). Pair with
        /// <c>UseGatewayRateLimiting()</c>.
        /// </summary>
        /// <param name="configuration">The application configuration.</param>
        /// <returns>The same service collection for chaining.</returns>
        public IServiceCollection AddGatewayRateLimiting(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);

            var section = configuration.GetSection(GatewayRateLimitingSettings.SectionName);
            services.Configure<GatewayRateLimitingSettings>(section);

            return services.AddGatewayRateLimiting(
                section.Get<GatewayRateLimitingSettings>() ?? new GatewayRateLimitingSettings());
        }

        /// <summary>
        /// Registers the edge rate limiter from already-built settings. The limiter composition,
        /// the partition keys and the bypass list are identical whatever the settings say; only the
        /// permit counts and the window change.
        /// </summary>
        /// <param name="settings">The gateway rate-limiting settings.</param>
        /// <returns>The same service collection for chaining.</returns>
        public IServiceCollection AddGatewayRateLimiting(GatewayRateLimitingSettings settings)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(settings);

            return services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

                // Chained, so a request must satisfy the per-IP window AND the replica-wide
                // concurrency ceiling. Assignment (not AddPolicy), so calling this twice replaces
                // the limiter rather than throwing on a duplicate policy name.
                options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                    PartitionedRateLimiter.Create<HttpContext, string>(
                        httpContext => ClientIpPartition(httpContext, settings)),
                    PartitionedRateLimiter.Create<HttpContext, string>(
                        httpContext => ConcurrencyPartition(httpContext, settings)));
            });
        }
    }

    extension(IApplicationBuilder app)
    {
        /// <summary>
        /// Adds the rate-limiting middleware to the pipeline. Place it AFTER
        /// <c>UseForwardedHeaders()</c> (so the client IP is the caller's, not the ingress's) and
        /// BEFORE the proxy is mapped.
        /// </summary>
        /// <returns>The same application builder for chaining.</returns>
        public IApplicationBuilder UseGatewayRateLimiting()
        {
            ArgumentNullException.ThrowIfNull(app);
            return app.UseRateLimiter();
        }
    }
}

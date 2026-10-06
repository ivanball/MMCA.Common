using System.Diagnostics.CodeAnalysis;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.UI.Web.SameOriginProxy;

namespace MMCA.Common.UI.Web.Hardening;

/// <summary>
/// Edge rate limiting for a Blazor Web host's own public origin: a per-client-IP fixed window
/// chained with a replica-wide concurrency ceiling, both rejecting with <c>429</c>.
/// </summary>
/// <remarks>
/// See <see cref="UiRateLimitingSettings"/> for why this origin needs its own limiter and why the
/// shape is shipped separately from the Gateway kit rather than referenced from it.
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with multiple extension(T) blocks in one static class, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class UiRateLimitingExtensions
{
    /// <summary>Partition key used for every request the limiters let through untouched.</summary>
    private const string ExemptPartitionKey = "__exempt";

    /// <summary>Partition key used when the client IP cannot be resolved (fail open).</summary>
    private const string UnknownIpPartitionKey = "__unknown-ip";

    /// <summary>Single partition key for the replica-wide concurrency ceiling.</summary>
    private const string ConcurrencyPartitionKey = "__ui";

    /// <summary>
    /// The Blazor circuit transport: negotiate, the circuit WebSocket and its long-polling fallback. A
    /// WebSocket request holds its rate-limiter lease for the lifetime of the circuit, so this prefix is
    /// kept out of the concurrency ceiling (circuits are bounded by <c>BlazorCircuitLimits</c> instead)
    /// while staying inside the per-IP window.
    /// </summary>
    private const string BlazorTransportPrefix = "/_blazor";

    /// <summary>
    /// Path prefixes that are never limited: the liveness and readiness probes (throttling them
    /// turns a traffic spike into a failed probe and a container restart), the two framework asset
    /// roots Blazor serves the WebAssembly runtime and every Razor class library's static web assets
    /// from, and <c>/hubs</c>. The hub prefix mirrors the Gateway's own
    /// <c>GatewayRateLimiting:BypassPathPrefixes</c>: a SignalR connection is long-lived and its
    /// negotiate and reconnect traffic must never be throttled, so a host that ever fronts a hub on
    /// this origin is covered by declaration rather than by accident. The same hub traffic arriving
    /// through the same-origin API proxy (<c>{SameOriginApiProxy:PathPrefix}/hubs</c>) is exempt too,
    /// at whatever prefix the host configured: without it every open hub WebSocket would hold a
    /// concurrency lease for its whole lifetime and exhaust the ceiling.
    /// </summary>
    private static readonly string[] ExemptPrefixes = ["/health", "/alive", "/_framework", "/_content", "/hubs"];

    /// <summary>
    /// Whether this request is exempt from both limiters.
    /// </summary>
    /// <param name="path">The request path.</param>
    /// <param name="proxyPathPrefix">The same-origin API proxy's path prefix (<c>/api</c> by default).</param>
    /// <returns><see langword="true"/> when the request must not be limited.</returns>
    /// <remarks>
    /// Two rules. The prefix list above (plus the proxied hub prefix), matched on whole segments so
    /// <c>/healthz</c> is not matched by <c>/health</c>; and any path OUTSIDE the proxy prefix whose
    /// last segment carries a file extension, which is how a static asset is told apart from a page
    /// route or the SignalR negotiate without depending on middleware ordering. Proxied traffic is
    /// API traffic whatever its last segment looks like (the proxy serves no static files), so
    /// <c>/api/report.csv</c> is counted. Static files are served from disk with an ETag and cost almost nothing,
    /// while a single page load pulls dozens of them: counting those against the window would
    /// throttle the first visitor rather than an attacker. <c>/_blazor</c> deliberately has NO
    /// extension and NO exemption from the per-IP window, because the negotiate endpoint is exactly what
    /// opens a circuit. It is, however, kept out of the concurrency ceiling (see
    /// <see cref="ConcurrencyPartition"/>): the same prefix carries the circuit WebSocket, whose lease
    /// would otherwise be held for the whole circuit lifetime.
    /// <para>
    /// Internal (not private) so the exemption rule is unit-testable via <c>InternalsVisibleTo</c>
    /// rather than only through a full request flood.
    /// </para>
    /// </remarks>
    internal static bool IsExempt(PathString path, PathString proxyPathPrefix)
    {
        if (ExemptPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            || path.StartsWithSegments(proxyPathPrefix.Add("/hubs"), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var value = path.Value;
        return !string.IsNullOrEmpty(value)
            && !path.StartsWithSegments(proxyPathPrefix, StringComparison.OrdinalIgnoreCase)
            && Path.HasExtension(value);
    }

    /// <summary>
    /// The per-client-IP partition: no limiter for exempt paths, no limiter for an unresolvable IP
    /// (fail open), otherwise one fixed window per IP.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="settings">The bound settings.</param>
    /// <param name="proxyPathPrefix">The same-origin API proxy's path prefix.</param>
    /// <returns>The partition this request counts against.</returns>
    /// <remarks>Internal so the partition key is unit-testable via <c>InternalsVisibleTo</c>.</remarks>
    internal static RateLimitPartition<string> ClientIpPartition(
        HttpContext httpContext,
        UiRateLimitingSettings settings,
        PathString proxyPathPrefix)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(settings);

        if (IsExempt(httpContext.Request.Path, proxyPathPrefix))
        {
            return RateLimitPartition.GetNoLimiter(ExemptPartitionKey);
        }

        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString();
        if (clientIp is null)
        {
            // Fail open rather than collapsing every unattributable request into one shared bucket,
            // which would throttle an in-process TestServer to a standstill.
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
    /// The replica-wide concurrency partition: one bucket for the whole process, exempt for the same
    /// paths the per-IP window exempts plus the Blazor circuit transport (<c>/_blazor</c>), whose
    /// WebSocket would otherwise hold one permit per open circuit and starve page loads. Circuits are
    /// bounded by <c>BlazorCircuitLimits:MaxActiveCircuits</c> instead.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="settings">The bound settings.</param>
    /// <param name="proxyPathPrefix">The same-origin API proxy's path prefix.</param>
    /// <returns>The partition this request counts against.</returns>
    /// <remarks>Internal so the partition key is unit-testable via <c>InternalsVisibleTo</c>.</remarks>
    internal static RateLimitPartition<string> ConcurrencyPartition(
        HttpContext httpContext,
        UiRateLimitingSettings settings,
        PathString proxyPathPrefix)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(settings);

        var path = httpContext.Request.Path;
        return IsExempt(path, proxyPathPrefix) || path.StartsWithSegments(BlazorTransportPrefix, StringComparison.OrdinalIgnoreCase)
            ? RateLimitPartition.GetNoLimiter(ExemptPartitionKey)
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
        /// Registers the UI host edge limiter from the <c>UiRateLimiting</c> configuration section.
        /// Pair with <c>UseUiRateLimiting()</c>, placed after forwarded headers so the partition key
        /// is the caller's IP rather than the ingress's.
        /// </summary>
        /// <param name="configuration">The application configuration.</param>
        /// <returns>The same service collection for chaining.</returns>
        public IServiceCollection AddUiRateLimiting(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);

            services.AddOptions<UiRateLimitingSettings>()
                .BindConfiguration(UiRateLimitingSettings.SectionName)
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // The limiter closes over the bound instance rather than resolving IOptions per request:
            // the partition callback is on the hot path of every single request, and an
            // out-of-range value has already failed the ValidateOnStart above.
            var settings = configuration.GetSection(UiRateLimitingSettings.SectionName)
                .Get<UiRateLimitingSettings>() ?? new UiRateLimitingSettings();

            services.AddRateLimiter(options => options.RejectionStatusCode = StatusCodes.Status429TooManyRequests);

            if (!settings.Enabled)
            {
                return services;
            }

            // The proxy prefix the hub exemption and the extension carve-out key on, read through
            // the proxy's own options (Bind plus any Configure<> the host adds), so the limiter and
            // the proxy can never disagree. Resolved once, when the limiter options are built.
            services.AddOptions<RateLimiterOptions>()
                .Configure<IOptions<SameOriginApiProxySettings>>((options, proxyOptions) =>
                {
                    var proxyPathPrefix = new PathString(proxyOptions.Value.PathPrefix);

                    // Chained, so a request must satisfy the per-IP window AND the replica-wide
                    // concurrency ceiling: they answer different questions.
                    options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                        PartitionedRateLimiter.Create<HttpContext, string>(
                            httpContext => ClientIpPartition(httpContext, settings, proxyPathPrefix)),
                        PartitionedRateLimiter.Create<HttpContext, string>(
                            httpContext => ConcurrencyPartition(httpContext, settings, proxyPathPrefix)));
                });

            return services;
        }
    }

    extension(IApplicationBuilder app)
    {
        /// <summary>
        /// Adds the rate-limiting middleware to the pipeline.
        /// </summary>
        /// <returns>The same application builder for chaining.</returns>
        public IApplicationBuilder UseUiRateLimiting()
        {
            ArgumentNullException.ThrowIfNull(app);
            return app.UseRateLimiter();
        }
    }
}

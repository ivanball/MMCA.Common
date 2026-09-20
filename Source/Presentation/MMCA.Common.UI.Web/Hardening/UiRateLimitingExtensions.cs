using System.Diagnostics.CodeAnalysis;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
    /// Path prefixes that are never limited: the liveness and readiness probes (throttling them
    /// turns a traffic spike into a failed probe and a container restart), the two framework asset
    /// roots Blazor serves the WebAssembly runtime and every Razor class library's static web assets
    /// from, and <c>/hubs</c>. The hub prefix mirrors the Gateway's own
    /// <c>GatewayRateLimiting:BypassPathPrefixes</c>: a SignalR connection is long-lived and its
    /// negotiate and reconnect traffic must never be throttled, so a host that ever fronts a hub on
    /// this origin is covered by declaration rather than by accident.
    /// </summary>
    private static readonly string[] ExemptPrefixes = ["/health", "/alive", "/_framework", "/_content", "/hubs"];

    /// <summary>
    /// Whether this request is exempt from both limiters.
    /// </summary>
    /// <param name="path">The request path.</param>
    /// <returns><see langword="true"/> when the request must not be limited.</returns>
    /// <remarks>
    /// Two rules. The prefix list above, matched on whole segments so <c>/healthz</c> is not matched
    /// by <c>/health</c>; and any path whose last segment carries a file extension, which is how a
    /// static asset is told apart from a page route or the SignalR negotiate without depending on
    /// middleware ordering. Static files are served from disk with an ETag and cost almost nothing,
    /// while a single page load pulls dozens of them: counting those against the window would
    /// throttle the first visitor rather than an attacker. <c>/_blazor</c> deliberately has NO
    /// extension and NO exemption, because the negotiate endpoint is exactly what opens a circuit.
    /// <para>
    /// Internal (not private) so the exemption rule is unit-testable via <c>InternalsVisibleTo</c>
    /// rather than only through a full request flood.
    /// </para>
    /// </remarks>
    internal static bool IsExempt(PathString path)
    {
        if (ExemptPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var value = path.Value;
        return !string.IsNullOrEmpty(value) && Path.HasExtension(value);
    }

    /// <summary>
    /// The per-client-IP partition: no limiter for exempt paths, no limiter for an unresolvable IP
    /// (fail open), otherwise one fixed window per IP.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="settings">The bound settings.</param>
    /// <returns>The partition this request counts against.</returns>
    /// <remarks>Internal so the partition key is unit-testable via <c>InternalsVisibleTo</c>.</remarks>
    internal static RateLimitPartition<string> ClientIpPartition(
        HttpContext httpContext,
        UiRateLimitingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(settings);

        if (IsExempt(httpContext.Request.Path))
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
    /// paths the per-IP window exempts.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="settings">The bound settings.</param>
    /// <returns>The partition this request counts against.</returns>
    /// <remarks>Internal so the partition key is unit-testable via <c>InternalsVisibleTo</c>.</remarks>
    internal static RateLimitPartition<string> ConcurrencyPartition(
        HttpContext httpContext,
        UiRateLimitingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(settings);

        return IsExempt(httpContext.Request.Path)
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

            return services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

                if (!settings.Enabled)
                {
                    return;
                }

                // Chained, so a request must satisfy the per-IP window AND the replica-wide
                // concurrency ceiling: they answer different questions.
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

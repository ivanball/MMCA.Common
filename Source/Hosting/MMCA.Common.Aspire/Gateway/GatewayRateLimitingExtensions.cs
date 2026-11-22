using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.RateLimiting;
using MMCA.Common.Shared.Resilience;

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
/// <para>
/// Three kinds of request take the no-limiter partition on BOTH limiters: an always-bypassed
/// infrastructure prefix, a host-configured bypass prefix, and a synthetic-traffic request proving
/// the configured secret in the configured header (off unless
/// <see cref="GatewayRateLimitingSettings.SyntheticTrafficSecret"/> is set). A trusted internal
/// caller proving <see cref="GatewayRateLimitingSettings.TrustedCallerSecret"/> (off unless that is
/// set) skips the concurrency ceiling and, by default
/// (<see cref="GatewayRateLimitingSettings.TrustedCallerMode"/>), is counted in the per-IP window of
/// the visitor it forwards in <c>X-Forwarded-For</c> rather than of its own address. That exists
/// because a server-rendered UI host makes every back-end call, refreshes included, from ONE
/// address, which the per-IP window would otherwise collapse into a single partition and throttle
/// the whole site.
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
    /// The <see cref="HttpContext.Items"/> key the per-IP partition stamps (value <see langword="true"/>)
    /// once a request has proven the trusted-internal-caller secret. The named per-route policies of
    /// <c>MMCA.Common.Gateway</c> read it (the same literal lives there as
    /// <c>GatewayRoutePolicyExtensions.TrustedInternalCallerItemKey</c>): the global limiter runs
    /// before the endpoint policy in the same middleware, and the Gateway package deliberately takes
    /// no reference to this one, so the proof travels on the request rather than being re-derived.
    /// </summary>
    internal const string TrustedInternalCallerItemKey = "MMCA.Common.Gateway.TrustedInternalCaller";

    /// <summary>
    /// The <see cref="HttpContext.Items"/> key the per-IP partition stamps with the visitor's
    /// <see cref="IPAddress"/> when a trusted caller is metered per visitor
    /// (<see cref="TrustedCallerMode.PerVisitor"/>). The named per-route policies of
    /// <c>MMCA.Common.Gateway</c> key on it instead of the connecting address (the same literal lives
    /// there as <c>GatewayRoutePolicyExtensions.TrustedCallerVisitorIpItemKey</c>).
    /// </summary>
    internal const string TrustedCallerVisitorIpItemKey = "MMCA.Common.Gateway.TrustedCallerVisitorIp";

    /// <summary>The header a trusted caller forwards the end visitor's address in.</summary>
    private const string ForwardedForHeaderName = "X-Forwarded-For";

    /// <summary>
    /// Wraps a rate limiter's <c>OnRejected</c> callback so every 429 carries a <c>Retry-After</c>
    /// header, in whole seconds rounded up, whenever the rejecting lease has a
    /// <see cref="MetadataName.RetryAfter"/> hint (the fixed-window limiter does; a concurrency
    /// rejection does not, and sends no header). A callback configured before this registration
    /// still runs, after the header is written.
    /// </summary>
    /// <param name="previous">The callback already configured, if any.</param>
    /// <returns>The callback to assign to <c>RateLimiterOptions.OnRejected</c>.</returns>
    internal static Func<OnRejectedContext, CancellationToken, ValueTask> WithRetryAfterHeader(
        Func<OnRejectedContext, CancellationToken, ValueTask>? previous) =>
        (context, cancellationToken) =>
        {
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            {
                context.HttpContext.Response.Headers.RetryAfter = RetryAfterHeader.Format(retryAfter);
            }

            return previous?.Invoke(context, cancellationToken) ?? ValueTask.CompletedTask;
        };

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
    /// Whether this request proves the configured synthetic-traffic secret and is therefore exempt
    /// from BOTH edge limiters. Always <see langword="false"/> when no secret is configured, so the
    /// bypass cannot be claimed by presenting the header alone.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="settings">The bound gateway settings.</param>
    /// <returns><see langword="true"/> when the request carries the configured secret.</returns>
    /// <remarks>
    /// The comparison runs over UTF-8 bytes through
    /// <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>,
    /// which is constant time for equal-length inputs and returns false for unequal lengths without
    /// leaking anything past the length an attacker already chose. Exactly one header value is
    /// required, so a caller cannot spray candidates in a multi-valued header. Internal (not
    /// private) so the check is unit-testable via <c>InternalsVisibleTo</c>.
    /// </remarks>
    internal static bool IsSyntheticTraffic(HttpContext httpContext, GatewayRateLimitingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(settings);

        return PresentsSecret(
            httpContext,
            settings.SyntheticTrafficHeaderName,
            settings.SyntheticTrafficSecret);
    }

    /// <summary>
    /// Whether this request proves the configured trusted-internal-caller secret and is therefore
    /// exempt from BOTH edge limiters. Always <see langword="false"/> when no secret is configured.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="settings">The bound gateway settings.</param>
    /// <returns><see langword="true"/> when the request carries the configured secret.</returns>
    /// <remarks>
    /// <para>
    /// Generalizes the synthetic-traffic bypass above from a load-test runner to any component the
    /// deployment trusts. The case it exists for is a server-rendered UI host: every one of its
    /// back-end calls, including the token refresh it makes on behalf of each signed-in visitor,
    /// leaves from ONE address, so the per-IP window collapses the whole site into one partition and
    /// starts answering 429 under normal load.
    /// </para>
    /// <para>
    /// The comparison runs over UTF-8 bytes through
    /// <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>
    /// and requires exactly one header value, exactly as the synthetic-traffic check does. Internal
    /// (not private) so the check is unit-testable via <c>InternalsVisibleTo</c>.
    /// </para>
    /// </remarks>
    internal static bool IsTrustedInternalCaller(HttpContext httpContext, GatewayRateLimitingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(settings);

        return PresentsSecret(
            httpContext,
            settings.TrustedCallerHeaderName,
            settings.TrustedCallerSecret);
    }

    /// <summary>
    /// Constant-time header-secret check shared by both bypasses: off when no secret is configured,
    /// and a multi-valued header never matches, so a caller cannot spray candidates.
    /// </summary>
    private static bool PresentsSecret(HttpContext httpContext, string headerName, string? expectedSecret)
    {
        if (string.IsNullOrWhiteSpace(expectedSecret) || string.IsNullOrWhiteSpace(headerName))
        {
            return false;
        }

        var presented = httpContext.Request.Headers[headerName];
        if (presented.Count != 1)
        {
            return false;
        }

        var presentedBytes = Encoding.UTF8.GetBytes(presented[0] ?? string.Empty);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedSecret);

        return CryptographicOperations.FixedTimeEquals(presentedBytes, expectedBytes);
    }

    /// <summary>
    /// Whether this request takes the no-limiter partition on both edge limiters: a bypassed path,
    /// proven synthetic traffic, or a proven trusted internal caller.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="settings">The bound gateway settings.</param>
    /// <returns><see langword="true"/> when neither limiter applies.</returns>
    internal static bool IsExemptFromLimiters(HttpContext httpContext, GatewayRateLimitingSettings settings) =>
        IsBypassed(httpContext.Request.Path, settings)
        || IsSyntheticTraffic(httpContext, settings)
        || IsTrustedInternalCaller(httpContext, settings);

    /// <summary>
    /// Per-client-IP partition: no limiter for bypassed paths or proven synthetic traffic; for a
    /// proven trusted internal caller, the forwarded visitor's own window under
    /// <see cref="TrustedCallerMode.PerVisitor"/> when a visitor address is present, otherwise no
    /// limiter; no limiter for an unresolvable IP (fail open); otherwise one fixed window per IP.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="settings">The bound gateway settings.</param>
    /// <param name="timeProvider">
    /// Clock for the window's remaining-time hint; <see langword="null"/> (production) is the system clock.
    /// </param>
    /// <returns>The partition this request counts against.</returns>
    /// <remarks>Internal (not private) so the partition-key selection is unit-testable.</remarks>
    internal static RateLimitPartition<string> ClientIpPartition(
        HttpContext httpContext,
        GatewayRateLimitingSettings settings,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(settings);

        if (IsTrustedInternalCaller(httpContext, settings))
        {
            var visitorIp = settings.TrustedCallerMode == TrustedCallerMode.PerVisitor
                ? TrustedCallerVisitorIp(httpContext)
                : null;

            if (visitorIp is null
                || IsBypassed(httpContext.Request.Path, settings)
                || IsSyntheticTraffic(httpContext, settings))
            {
                // Hand the proof to the named per-route policies, which run after this limiter and
                // cannot see these settings.
                httpContext.Items[TrustedInternalCallerItemKey] = true;
                return RateLimitPartition.GetNoLimiter(BypassPartitionKey);
            }

            // The visitor's own window: the same partition key a direct request from that address
            // takes. The route policies key on the same address through this item.
            httpContext.Items[TrustedCallerVisitorIpItemKey] = visitorIp;
            return FixedWindowPartition(visitorIp.ToString(), settings, timeProvider);
        }

        if (IsExemptFromLimiters(httpContext, settings))
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

        return FixedWindowPartition(clientIp, settings, timeProvider);
    }

    /// <summary>
    /// The end visitor's address a trusted caller forwards: the right-most entry of
    /// <c>X-Forwarded-For</c> as it reaches the limiter. The forwarded-headers step that runs first
    /// has already consumed the hop the gateway's own ingress appended, so the right-most remaining
    /// entry is the one the trusted caller wrote (a server-rendered UI host writes exactly one, the
    /// visitor's address). <see langword="null"/> when the header is absent or does not parse as an
    /// address.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <returns>The visitor's address, or <see langword="null"/> when none is present.</returns>
    /// <remarks>Internal (not private) so the extraction is unit-testable via <c>InternalsVisibleTo</c>.</remarks>
    internal static IPAddress? TrustedCallerVisitorIp(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var forwardedFor = httpContext.Request.Headers[ForwardedForHeaderName];
        if (forwardedFor.Count == 0)
        {
            return null;
        }

        var lastEntry = string.Join(',', forwardedFor.ToArray())
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        return lastEntry is not null && IPEndPoint.TryParse(lastEntry, out var endpoint)
            ? endpoint.Address
            : null;
    }

    /// <summary>One fixed window per partition key, sized by the bound settings.</summary>
    private static RateLimitPartition<string> FixedWindowPartition(
        string partitionKey,
        GatewayRateLimitingSettings settings,
        TimeProvider? timeProvider) =>
        // Not RateLimitPartition.GetFixedWindowLimiter: the framework limiter's Retry-After is always the
        // whole window, so the wrapper reports the time actually left in it. The partitioned limiter
        // drives replenishment on its own timer either way (it never lets a partition auto-replenish).
        RateLimitPartition.Get(partitionKey, _ => new RemainingWindowFixedWindowRateLimiter(
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = settings.PermitLimit,
                Window = TimeSpan.FromSeconds(settings.WindowSeconds),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            },
            timeProvider));

    /// <summary>
    /// Process-wide concurrency partition: one bucket for the whole replica, bypassed for the same
    /// paths, the same proven synthetic traffic and every proven trusted internal caller (in both
    /// <see cref="TrustedCallerMode"/> values: the ceiling bounds replica load, not a visitor).
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

        return IsExemptFromLimiters(httpContext, settings)
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
            services.AddOptions<GatewayRateLimitingSettings>()
                .Bind(section)
                .ValidateDataAnnotations()
                .ValidateOnStart();

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

            // The limiter closes over this settings instance rather than resolving IOptions per
            // request, so the fail-fast contract (ADR-070) is honored here directly: an
            // out-of-range value throws at registration, not at first throttle.
            Validator.ValidateObject(settings, new ValidationContext(settings), validateAllProperties: true);

            return services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

                // OnRejected is one shared slot on RateLimiterOptions, so the route-policy registration
                // in MMCA.Common.Gateway installs the same header writer; each chains the one before it.
                options.OnRejected = WithRetryAfterHeader(options.OnRejected);

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

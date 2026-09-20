using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.API.RateLimiting;
using MMCA.Common.Shared.Auth;
using StackExchange.Redis;

namespace MMCA.Common.API.Startup;

public static partial class WebApplicationBuilderExtensions
{
    /// <summary>
    /// Named rate-limit policy throttling ANONYMOUS authentication attempts per client IP. Apply it
    /// with <c>[EnableRateLimiting(WebApplicationBuilderExtensions.RateLimitPolicyAuthIp)]</c> on
    /// login/register actions. It exists because the global limiter deliberately no-ops for
    /// anonymous traffic and per-account lockout is per-email, which leaves a password spray (one
    /// password, many emails) from a single source otherwise unthrottled.
    /// </summary>
    public const string RateLimitPolicyAuthIp = "auth-ip";

    /// <summary>True for traffic that must bypass rate limiting: health/liveness probes, JWKS
    /// discovery, and gRPC inter-service calls — all legitimately high-frequency.</summary>
    /// <remarks>
    /// <para>
    /// The gRPC arm is keyed on the ROUTED ENDPOINT, never on the request's <c>Content-Type</c>
    /// (SEC-Common-44). A header is caller-supplied and unverifiable: stamping
    /// <c>Content-Type: application/grpc</c> on ordinary requests used to hand any authenticated
    /// account the no-limiter partition and switch off its 300/min cap entirely. Endpoint metadata
    /// is produced by routing from the server's own <c>MapGrpcService</c> registrations, so it
    /// cannot be forged. This predicate runs from the rate-limiting middleware, which sits AFTER
    /// <c>UseRouting</c> (see <c>MiddlewarePipelineBuilder</c>), so the endpoint is already resolved.
    /// </para>
    /// <para>
    /// Internal (not private) so the partition/exemption logic is unit-testable via
    /// <c>InternalsVisibleTo</c> rather than only through a full request flood.
    /// </para>
    /// </remarks>
    internal static bool IsRateLimitBypassed(HttpContext httpContext) =>
        httpContext.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
        || httpContext.Request.Path.StartsWithSegments("/alive", StringComparison.OrdinalIgnoreCase)
        || httpContext.Request.Path.StartsWithSegments("/.well-known", StringComparison.OrdinalIgnoreCase)
        || IsGrpcEndpoint(httpContext);

    /// <summary>
    /// The partition an UNAUTHENTICATED request counts against: none, except on a real-time hub
    /// path, where it is metered per client IP (SEC-ADC-25).
    /// </summary>
    /// <remarks>
    /// Anonymous traffic is exempt everywhere else on purpose: public reads are output-cached and
    /// server-rendered Blazor traffic shares one host IP. A gateway that bypasses <c>/hubs</c> at the
    /// edge (ADR-024 requires it, because the hub authenticates from a query-string token the edge
    /// cannot read) left an anonymous negotiate flood metered nowhere at all.
    /// </remarks>
    private static RateLimitPartition<string> AnonymousPartition(HttpContext httpContext, RateLimitingSettings settings) =>
        IsAnonymousHubRequest(httpContext, settings)
            ? CreateLimitedPartition(
                httpContext,
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous-hub",
                redisScope: "hub",
                permitLimit: settings.AnonymousHubPermitLimit,
                queueLimit: 0,
                settings,
                allowDistributed: true)
            : RateLimitPartition.GetNoLimiter("__anonymous");

    /// <summary>
    /// Whether an unauthenticated request targets one of the configured hub path prefixes
    /// (SEC-ADC-25).
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="settings">The bound rate-limiting settings.</param>
    /// <returns><see langword="true"/> when the anonymous request must be metered per client IP.</returns>
    /// <remarks>Internal so the path match is unit-testable via <c>InternalsVisibleTo</c>.</remarks>
    internal static bool IsAnonymousHubRequest(HttpContext httpContext, RateLimitingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(settings);

        var path = httpContext.Request.Path;

        return settings.HubPathPrefixes.Any(prefix =>
            !string.IsNullOrWhiteSpace(prefix)
            && path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The namespace every metadata type Grpc.AspNetCore.Server attaches to a mapped gRPC method
    /// lives in. Matched by name so MMCA.Common.API needs no package reference on the gRPC server
    /// stack, which only the extracted service hosts take.
    /// </summary>
    private const string GrpcServerMetadataNamespace = "Grpc.AspNetCore.Server";

    /// <summary>
    /// Whether the request routed to a mapped gRPC method.
    /// </summary>
    /// <param name="httpContext">The request, after routing.</param>
    /// <returns><see langword="true"/> only for an endpoint the server itself mapped as gRPC.</returns>
    /// <remarks>Internal so the metadata match is unit-testable.</remarks>
    internal static bool IsGrpcEndpoint(HttpContext httpContext)
    {
        var endpoint = httpContext.GetEndpoint();
        if (endpoint is null)
        {
            return false;
        }

        foreach (var metadata in endpoint.Metadata)
        {
            var metadataNamespace = metadata?.GetType().Namespace;
            if (metadataNamespace is not null
                && metadataNamespace.StartsWith(GrpcServerMetadataNamespace, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Global rate-limit partition: bypasses infrastructure traffic and anonymous requests,
    /// and limits authenticated callers per user (name → subject claim → IP).</summary>
    /// <remarks>Internal (not private) so the partition-key selection is unit-testable via
    /// <c>InternalsVisibleTo</c>.</remarks>
    internal static RateLimitPartition<string> GlobalRateLimitPartition(HttpContext httpContext, int globalPermitLimit) =>
        GlobalRateLimitPartition(httpContext, new RateLimitingSettings { GlobalPermitLimit = globalPermitLimit });

    /// <summary>Global rate-limit partition, configured from <see cref="RateLimitingSettings"/>:
    /// same exemptions and same partition keys as the permit-count overload, with the algorithm and
    /// the distributed-counter choice taken from settings.</summary>
    /// <remarks>Internal (not private) so the partition-key selection is unit-testable via
    /// <c>InternalsVisibleTo</c>.</remarks>
    internal static RateLimitPartition<string> GlobalRateLimitPartition(HttpContext httpContext, RateLimitingSettings settings)
    {
        if (IsRateLimitBypassed(httpContext))
        {
            return RateLimitPartition.GetNoLimiter("__infra");
        }

        if (httpContext.User?.Identity?.IsAuthenticated != true)
        {
            return AnonymousPartition(httpContext, settings);
        }

        var partitionKey = httpContext.User.Identity.Name
            ?? httpContext.User.FindUserIdValue()
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "authenticated";

        return CreateLimitedPartition(
            httpContext,
            partitionKey,
            redisScope: "global",
            permitLimit: settings.GlobalPermitLimit,
            queueLimit: 0,
            settings,
            allowDistributed: true);
    }

    /// <summary>
    /// Partition selector for the opt-in "UserPolicy" limiter: one bucket per authenticated user,
    /// falling back to the client IP and then to a shared anonymous bucket.
    /// </summary>
    /// <remarks>
    /// Extracted from the inline lambda it used to be so the key selection is unit-testable via
    /// <c>InternalsVisibleTo</c>, exactly like <see cref="GlobalRateLimitPartition(HttpContext, RateLimitingSettings)"/>.
    /// </remarks>
    internal static RateLimitPartition<string> UserPolicyRateLimitPartition(HttpContext httpContext, RateLimitingSettings settings)
    {
        var partitionKey = httpContext.User?.Identity?.Name
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous";

        return CreateLimitedPartition(
            httpContext,
            partitionKey,
            redisScope: "user",
            permitLimit: settings.PerUserPermitLimit,
            queueLimit: settings.QueueLimit,
            settings,
            allowDistributed: true);
    }

    /// <summary>
    /// Builds one limited partition for <paramref name="partitionKey"/>, choosing between the
    /// shared Redis counter and the in-memory fixed or sliding window.
    /// </summary>
    /// <param name="httpContext">The request, used only to resolve services.</param>
    /// <param name="partitionKey">The partition key, unchanged from what the caller computed.</param>
    /// <param name="redisScope">
    /// Prefix applied to the Redis key only, so the global limiter and "UserPolicy" never share a
    /// counter for the same user while both keep their original partition keys.
    /// </param>
    /// <param name="permitLimit">Permits per one-minute window for this partition.</param>
    /// <param name="queueLimit">Queued requests allowed once the partition is saturated.</param>
    /// <param name="settings">The bound rate-limiting settings.</param>
    /// <param name="allowDistributed">
    /// Whether this partition may use the Redis counter. False for the <c>auth-ip</c> policy, which
    /// stays per-instance: per-account login protection already backs it, and a login throttle that
    /// fails open on a Redis outage is a worse trade than one that stays local.
    /// </param>
    private static RateLimitPartition<string> CreateLimitedPartition(
        HttpContext httpContext,
        string partitionKey,
        string redisScope,
        int permitLimit,
        int queueLimit,
        RateLimitingSettings settings,
        bool allowDistributed)
    {
        if (allowDistributed && settings.Distributed)
        {
            // RequestServices is declared non-nullable but is genuinely null outside a request
            // pipeline (a bare DefaultHttpContext in a unit test), so it is read through a nullable
            // local rather than dereferenced.
            IServiceProvider? requestServices = httpContext.RequestServices;
            var connection = requestServices?.GetService<IConnectionMultiplexer>();

            if (connection is not null)
            {
                var logger = (ILogger?)requestServices?.GetService<ILogger<RedisFixedWindowRateLimiter>>()
                    ?? NullLogger<RedisFixedWindowRateLimiter>.Instance;

                return RateLimitPartition.Get(
                    partitionKey,
                    key => new RedisFixedWindowRateLimiter(connection, $"{redisScope}:{key}", permitLimit, logger));
            }

            // No multiplexer registered: fall through to the in-memory limiters rather than failing
            // startup, so a host that turns the flag on before wiring Redis degrades to
            // per-instance limits instead of losing rate limiting altogether.
        }

        if (settings.Algorithm == RateLimitAlgorithm.SlidingWindow)
        {
            return RateLimitPartition.GetSlidingWindowLimiter(partitionKey, _ => new SlidingWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = settings.SegmentsPerWindow,
                PermitLimit = permitLimit,
                QueueLimit = queueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            });
        }

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromMinutes(1),
            PermitLimit = permitLimit,
            QueueLimit = queueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    }

    /// <summary>
    /// Partition selector for <see cref="RateLimitPolicyAuthIp"/>: a fixed window keyed on the
    /// client IP, or no limiter at all when the IP is unattributable.
    /// </summary>
    /// <remarks>
    /// Internal (not private) for the same reason as
    /// <see cref="GlobalRateLimitPartition(HttpContext, int)"/>: the
    /// load-bearing decision (fail open on a null IP rather than collapsing every such request into
    /// one shared bucket, which would throttle the in-process TestServer and the integration tier to
    /// a standstill) is worth asserting directly rather than only through a request flood.
    /// </remarks>
    internal static RateLimitPartition<string> AuthIpRateLimitPartition(HttpContext httpContext, int authIpPermitLimit) =>
        AuthIpRateLimitPartition(httpContext, new RateLimitingSettings { AuthIpPermitLimit = authIpPermitLimit });

    /// <summary>
    /// Partition selector for <see cref="RateLimitPolicyAuthIp"/>, configured from
    /// <see cref="RateLimitingSettings"/>. Honors the configured algorithm but never the
    /// distributed counter: see the <c>allowDistributed</c> note on the partition factory.
    /// </summary>
    /// <remarks>Internal (not private) for the same reason as the permit-count overload.</remarks>
    internal static RateLimitPartition<string> AuthIpRateLimitPartition(HttpContext httpContext, RateLimitingSettings settings)
    {
        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString();

        return clientIp is null
            ? RateLimitPartition.GetNoLimiter("__unknown-ip")
            : CreateLimitedPartition(
                httpContext,
                clientIp,
                redisScope: "auth-ip",
                permitLimit: settings.AuthIpPermitLimit,
                queueLimit: 0,
                settings,
                allowDistributed: false);
    }

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers rate limiting. A <b>global</b> limiter (active on every request through
        /// <c>UseRateLimiter</c>) rejects with <c>429</c> over <paramref name="globalPermitLimit"/>
        /// requests/minute <b>per authenticated user</b>. It deliberately does <b>not</b> limit
        /// anonymous traffic — public endpoints are output-cached, login brute-force is handled by
        /// the login-protection service, and anonymous server-rendered (Blazor Server) traffic shares
        /// the UI host's IP, so an IP partition would throttle legitimate public browsing at scale.
        /// Health/liveness (<c>/health</c>, <c>/alive</c>), JWKS discovery (<c>/.well-known/*</c>) and
        /// gRPC inter-service traffic (<c>application/grpc</c>) are bypassed — they legitimately run
        /// at high frequency. The named "FixedPolicy"/"UserPolicy" limiters remain for opt-in
        /// <c>[EnableRateLimiting]</c> use, as does <see cref="RateLimitPolicyAuthIp"/>, the per-IP
        /// anonymous-authentication throttle described on <paramref name="authIpPermitLimit"/>.
        /// <para>
        /// This overload keeps every other knob at its default. To reach the sliding-window
        /// algorithm or the shared Redis counter, use the <see cref="IConfiguration"/> overload and
        /// a <c>RateLimiting</c> configuration section (<see cref="RateLimitingSettings"/>).
        /// </para>
        /// </summary>
        /// <param name="permitLimit">Requests per minute for the opt-in "FixedPolicy" limiter.</param>
        /// <param name="queueLimit">Queued requests allowed once "FixedPolicy"/"UserPolicy" are saturated.</param>
        /// <param name="perUserPermitLimit">Requests per minute per user for the opt-in "UserPolicy" limiter.</param>
        /// <param name="globalPermitLimit">Requests per minute per authenticated user for the always-on global limiter.</param>
        /// <param name="authIpPermitLimit">
        /// Requests per minute per client IP for the <see cref="RateLimitPolicyAuthIp"/> policy.
        /// Default 30 rather than a tighter 10 on purpose: Blazor Server interactive circuits issue
        /// the login HTTP call SERVER-side, so every Server-circuit user shares the UI host's IP. A
        /// login burst (and a sequential E2E gate) must fit inside the window, while a spray still
        /// drops from unlimited to roughly 43K attempts/day/IP with per-account lockout intact on
        /// top. Tighten toward 10 only once real client IPs are forwarded end to end.
        /// </param>
        public IServiceCollection AddCommonRateLimiting(int permitLimit = 100, int queueLimit = 2, int perUserPermitLimit = 30, int globalPermitLimit = 300, int authIpPermitLimit = 30) =>
            services.AddCommonRateLimiting(new RateLimitingSettings
            {
                PermitLimit = permitLimit,
                QueueLimit = queueLimit,
                PerUserPermitLimit = perUserPermitLimit,
                GlobalPermitLimit = globalPermitLimit,
                AuthIpPermitLimit = authIpPermitLimit,
            });

        /// <summary>
        /// Registers rate limiting from the <c>RateLimiting</c> configuration section. Equivalent to
        /// the permit-count overload when the section is absent, and the only way to reach the
        /// sliding-window algorithm and the shared Redis counter
        /// (<see cref="RateLimitingSettings.Algorithm"/>, <see cref="RateLimitingSettings.Distributed"/>).
        /// </summary>
        /// <param name="configuration">The application configuration.</param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddCommonRateLimiting(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            var settings = configuration.GetSection(RateLimitingSettings.SectionName).Get<RateLimitingSettings>()
                ?? new RateLimitingSettings();

            return services.AddCommonRateLimiting(settings);
        }

        /// <summary>
        /// Registers rate limiting from an already-built <see cref="RateLimitingSettings"/>. The
        /// policy names ("FixedPolicy", "UserPolicy", <see cref="RateLimitPolicyAuthIp"/>), the
        /// bypass list and every partition key are identical whatever the settings say; only the
        /// permit counts, the algorithm and the counter's location change.
        /// </summary>
        /// <param name="settings">The rate-limiting settings.</param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddCommonRateLimiting(RateLimitingSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            return services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                    httpContext => GlobalRateLimitPartition(httpContext, settings));

                // "FixedPolicy" keeps its name whichever algorithm is configured: it is an opt-in
                // policy referenced by name from [EnableRateLimiting] attributes in three repos, so
                // renaming it on a settings change would silently unlimit every endpoint using it.
                options.AddPolicy("FixedPolicy", httpContext => CreateLimitedPartition(
                    httpContext,
                    partitionKey: "__fixed",
                    redisScope: "fixed",
                    permitLimit: settings.PermitLimit,
                    queueLimit: settings.QueueLimit,
                    settings,
                    allowDistributed: false));

                options.AddPolicy("UserPolicy", httpContext => UserPolicyRateLimitPartition(httpContext, settings));

                // Per-IP anonymous authentication throttle. Client IP is taken from
                // Connection.RemoteIpAddress, which the shared pipeline has already resolved from
                // X-Forwarded-For (UseForwardedHeaders runs before UseRateLimiter), the same
                // canonical source the global partition uses. A null RemoteIpAddress (in-process
                // TestServer, integration tests) is NOT limited, mirroring the global limiter's
                // fail-open posture for unattributable traffic. Endpoints opt in with
                // [EnableRateLimiting(RateLimitPolicyAuthIp)], so health, JWKS and gRPC are
                // untouched.
                options.AddPolicy(
                    RateLimitPolicyAuthIp,
                    httpContext => AuthIpRateLimitPartition(httpContext, settings));
            });
        }
    }
}

using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Asp.Versioning;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.OpenApi;
using MMCA.Common.API.RateLimiting;
using MMCA.Common.Infrastructure.Settings;
using MMCA.Common.Shared.Auth;
using StackExchange.Redis;

namespace MMCA.Common.API.Startup;

/// <summary>
/// Shared WebAPI service registration extensions used by all downstream MMCA applications.
/// Consolidates identical builder-side setup (versioning, rate limiting, compression, CORS).
/// </summary>
public static class WebApplicationBuilderExtensions
{
    /// <summary>Default CORS policy name for production (allowed origins from config).</summary>
    public const string CorsPolicyAllowSpecificOrigins = "_allowSpecificOrigins";

    /// <summary>Default CORS policy name for development (any origin).</summary>
    public const string CorsPolicyAllowAll = "_allowAll";

    /// <summary>
    /// Named rate-limit policy throttling ANONYMOUS authentication attempts per client IP. Apply it
    /// with <c>[EnableRateLimiting(WebApplicationBuilderExtensions.RateLimitPolicyAuthIp)]</c> on
    /// login/register actions. It exists because the global limiter deliberately no-ops for
    /// anonymous traffic and per-account lockout is per-email, which leaves a password spray (one
    /// password, many emails) from a single source otherwise unthrottled.
    /// </summary>
    public const string RateLimitPolicyAuthIp = "auth-ip";

    /// <summary>
    /// Configuration key that overrides <c>AddForwardedJwtBearer</c>'s secure-by-default
    /// <c>RequireHttpsMetadata</c>. Set it to <see langword="false"/> only for a deployment whose
    /// authority is genuinely plain HTTP (an internal-ingress h2c service URL), and record the
    /// justification beside the setting in the deployment template.
    /// </summary>
    public const string RequireHttpsMetadataConfigKey = "Authentication:JwtBearer:RequireHttpsMetadata";

    /// <summary>True for traffic that must bypass rate limiting: health/liveness probes, JWKS
    /// discovery, and gRPC inter-service calls — all legitimately high-frequency.</summary>
    /// <remarks>Internal (not private) so the partition/exemption logic is unit-testable via
    /// <c>InternalsVisibleTo</c> rather than only through a full request flood.</remarks>
    internal static bool IsRateLimitBypassed(HttpContext httpContext) =>
        httpContext.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
        || httpContext.Request.Path.StartsWithSegments("/alive", StringComparison.OrdinalIgnoreCase)
        || httpContext.Request.Path.StartsWithSegments("/.well-known", StringComparison.OrdinalIgnoreCase)
        || (httpContext.Request.ContentType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) ?? false);

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
            return RateLimitPartition.GetNoLimiter("__anonymous");
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
        /// Registers header-based API versioning (v1.0 default) with MVC and API explorer support.
        /// Also installs <see cref="ApiParameterDescriptorBackfillProvider"/>, which keeps
        /// URL-segment-versioned routes from failing OpenAPI document generation.
        /// </summary>
        public IServiceCollection AddCommonApiVersioning()
        {
            // DefaultApiVersion is deliberately not set: 1.0 is already the framework default, and
            // the API explorer inherits both it and AssumeDefaultVersionWhenUnspecified from the
            // versioning options below (restating either one trips AV0011/AV0024).
            services.AddApiVersioning(options =>
            {
                options.AssumeDefaultVersionWhenUnspecified = true;
                options.ReportApiVersions = true;
                options.ApiVersionReader = new HeaderApiVersionReader("api-version");
            }).AddMvc()
            .AddApiExplorer(options =>
            {
                options.GroupNameFormat = "'v'VVV";
                options.SubstituteApiVersionInUrl = true;
            });

            services.AddApiParameterDescriptorBackfill();

            return services;
        }

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

        /// <summary>
        /// Registers Brotli + Gzip response compression for HTTPS responses.
        /// </summary>
        public IServiceCollection AddCommonResponseCompression()
        {
            services.AddResponseCompression(options =>
            {
                options.EnableForHttps = true;
                options.Providers.Add<BrotliCompressionProvider>();
                options.Providers.Add<GzipCompressionProvider>();
            });
            services.Configure<BrotliCompressionProviderOptions>(options =>
                options.Level = CompressionLevel.Fastest);
            // Fastest for gzip too: these are dynamic per-request API payloads on fractional
            // vCPUs, so the CPU cost of SmallestSize outweighs the marginal size win for the
            // gzip-only-client minority (Brotli-capable clients never hit this provider).
            services.Configure<GzipCompressionProviderOptions>(options =>
                options.Level = CompressionLevel.Fastest);

            return services;
        }

        /// <summary>
        /// Registers OpenAPI document generation through the API-versioning builder, which creates
        /// one document per discovered API version named by the API explorer's
        /// <c>GroupNameFormat</c> (<c>'v'VVV</c>, so v1.0 is the <c>v1</c> document). Pair with
        /// <c>MapCommonOpenApi()</c> so each service serves <c>/openapi/v1.json</c> the same way.
        /// The parameterless <c>AddApiVersioning()</c> call only returns the builder; the options
        /// configured by <c>AddCommonApiVersioning</c> accumulate independently of call order.
        /// Also installs <see cref="ApiParameterDescriptorBackfillProvider"/>, so a host that opts
        /// into OpenAPI without calling <c>AddCommonApiVersioning</c> is guarded too.
        /// </summary>
        public IServiceCollection AddCommonOpenApi()
        {
            services.AddApiVersioning().AddOpenApi();
            services.AddApiParameterDescriptorBackfill();

            return services;
        }

        /// <summary>
        /// Registers the <see cref="ApiParameterDescriptorBackfillProvider"/> guard exactly once,
        /// however many of the registration helpers above a host calls.
        /// <see cref="ServiceCollectionDescriptorExtensions.TryAddEnumerable(IServiceCollection, ServiceDescriptor)"/>
        /// de-duplicates on the implementation type, so the repeated call is a no-op rather than a
        /// second pass over every API description.
        /// </summary>
        private void AddApiParameterDescriptorBackfill() =>
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<IApiDescriptionProvider, ApiParameterDescriptorBackfillProvider>());

        /// <summary>
        /// Registers JWT Bearer authentication that trusts an external Identity service's JWKS
        /// endpoint via OIDC-style authority discovery. Use this in extracted microservices
        /// (everything except the Identity service itself) so they validate tokens issued by
        /// the central Identity service without sharing a symmetric secret.
        /// <para>
        /// The <paramref name="authority"/> argument is the base URL of the Identity service
        /// (e.g. <c>http://identity</c>, resolved via Aspire service discovery). The JWT
        /// middleware fetches <c>{authority}/.well-known/openid-configuration</c> on startup,
        /// which in turn points at <c>/.well-known/jwks.json</c> served by <c>MapJwksEndpoint</c>.
        /// </para>
        /// </summary>
        /// <param name="authority">The Identity service base URL (no trailing slash).</param>
        /// <param name="audience">The expected JWT audience claim.</param>
        /// <param name="configuration">The application configuration, read for <see cref="RequireHttpsMetadataConfigKey"/>.</param>
        /// <param name="environment">The host environment, which decides the default when nothing overrides it.</param>
        /// <param name="requireHttpsMetadata">
        /// Whether the metadata fetch must use HTTPS. Resolved in three steps: this argument when it
        /// is not <see langword="null"/>, then <see cref="RequireHttpsMetadataConfigKey"/>, then
        /// <see langword="true"/> everywhere except Development. A resolved
        /// <see langword="false"/> outside Development is legal (an internal-ingress h2c authority
        /// is the reason it stays reachable) but logs one startup warning naming the config key.
        /// </param>
        public IServiceCollection AddForwardedJwtBearer(
            string authority,
            string audience,
            IConfiguration configuration,
            IHostEnvironment environment,
            bool? requireHttpsMetadata = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(authority);
            ArgumentException.ThrowIfNullOrWhiteSpace(audience);
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(environment);

            var resolvedRequireHttpsMetadata = requireHttpsMetadata
                ?? configuration.GetValue<bool?>(RequireHttpsMetadataConfigKey)
                ?? !environment.IsDevelopment();

            if (!resolvedRequireHttpsMetadata && !environment.IsDevelopment())
            {
                services.TryAddEnumerable(
                    ServiceDescriptor.Singleton<IStartupFilter, InsecureJwtMetadataWarningStartupFilter>());
            }

            return services.AddForwardedJwtBearerCore(authority, audience, resolvedRequireHttpsMetadata);
        }

        private IServiceCollection AddForwardedJwtBearerCore(
            string authority,
            string audience,
            bool resolvedRequireHttpsMetadata)
        {
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.Authority = authority;
                    options.Audience = audience;
                    options.RequireHttpsMetadata = resolvedRequireHttpsMetadata;

                    options.TokenValidationParameters = new()
                    {
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        // ValidIssuer is intentionally NOT set here. The JWT bearer
                        // middleware derives it from the OIDC discovery document's
                        // "issuer" field (served by MapOidcDiscoveryEndpoint). This
                        // avoids a mismatch: authority is the Aspire service-discovery
                        // URL (e.g. "http://identity") while the token's iss claim is
                        // the public gateway URL (e.g. "https://localhost:6001").
                        ValidAudience = audience,
                        // Pin the signature algorithm to RS256. The JWKS path only ever
                        // validates Identity's asymmetric (RS256) tokens, so this is
                        // defense-in-depth against an algorithm-confusion swap (e.g.
                        // forging an HS256 token using the RSA public key as the HMAC
                        // secret), matching the explicit pin on the in-process
                        // BuildValidationParameters path.
                        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    };

                    // Same SignalR access_token query-string fallback as AddCommonAuthentication.
                    options.Events = new JwtBearerEvents
                    {
                        OnMessageReceived = context =>
                        {
                            var accessToken = context.Request.Query["access_token"];
                            if (!string.IsNullOrEmpty(accessToken)
                                && context.HttpContext.Request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
                            {
                                context.Token = accessToken;
                            }

                            return Task.CompletedTask;
                        }
                    };
                });

            services.AddAuthorizationPolicies();
            return services;
        }

        /// <summary>
        /// Registers JWT Bearer authentication and authorization policies, supporting both
        /// symmetric (HMAC-SHA256) and asymmetric (RSA-SHA256) signing modes selected via
        /// <see cref="JwtSettings.SigningAlgorithm"/>.
        /// <para>
        /// In the default <see cref="JwtSigningAlgorithm.RS256"/> mode, the validator loads the
        /// RSA public key from <see cref="JwtSettings.RsaPublicKeyPem"/>. In
        /// <see cref="JwtSigningAlgorithm.HS256"/> mode, a single-process monolith's validator uses
        /// the same Base64 HMAC secret as the issuer.
        /// For extracted services that should fetch the public key from the Identity
        /// service's JWKS endpoint at runtime, use <c>AddForwardedJwtBearer</c> instead.
        /// </para>
        /// </summary>
        public IServiceCollection AddCommonAuthentication(IConfiguration configuration)
        {
            services.AddOptions<JwtSettings>()
                .Bind(configuration.GetSection(JwtSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    var jwtSettings = configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>()
                        ?? throw new InvalidOperationException("JwtSettings section is not configured.");

                    options.TokenValidationParameters = BuildValidationParameters(jwtSettings);

                    // SignalR WebSocket connections cannot send HTTP headers — the JWT is
                    // passed as an "access_token" query-string parameter instead. Extract
                    // it here so the standard JWT middleware can authenticate hub requests.
                    options.Events = new JwtBearerEvents
                    {
                        OnMessageReceived = context =>
                        {
                            var accessToken = context.Request.Query["access_token"];
                            if (!string.IsNullOrEmpty(accessToken)
                                && context.HttpContext.Request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
                            {
                                context.Token = accessToken;
                            }

                            return Task.CompletedTask;
                        }
                    };
                });

            services.AddAuthorizationPolicies();

            return services;
        }

        /// <summary>
        /// Registers two CORS policies: a restrictive one for production (origins from
        /// <c>Cors:AllowedOrigins</c> configuration) and an open one for development.
        /// </summary>
        public IServiceCollection AddCommonCors(IConfiguration configuration)
        {
            services.AddCors(options =>
            {
                options.AddPolicy(CorsPolicyAllowSpecificOrigins, policy =>
                {
                    var allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
                    policy.WithOrigins(allowedOrigins)
                          .WithHeaders("Content-Type", "Authorization", "x-signalr-user-agent", "x-requested-with")
                          .WithMethods("GET", "POST", "PUT", "DELETE", "PATCH")
                          .AllowCredentials();
                });
#pragma warning disable S5122 // Allow-any-origin policy is only ever selected when app.Environment.IsDevelopment() (see WebApplicationExtensions.UseCommonMiddlewarePipeline); production uses CorsPolicyAllowSpecificOrigins above
                options.AddPolicy(CorsPolicyAllowAll, policy =>
                    policy.AllowAnyOrigin()
                          .AllowAnyHeader()
                          .AllowAnyMethod());
#pragma warning restore S5122
            });

            return services;
        }
    }

    /// <summary>
    /// Decodes a Base64-encoded JWT signing key and validates that it meets the
    /// minimum length requirement for HMAC-SHA256 (256 bits / 32 bytes).
    /// </summary>
    internal static byte[] GetValidatedSigningKey(string base64Key)
    {
        var keyBytes = Convert.FromBase64String(base64Key);
        if (keyBytes.Length < 32)
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"JWT SecretForKey must be at least 256 bits (32 bytes) for HMAC-SHA256. Current key is {keyBytes.Length * 8} bits."));
        }

        return keyBytes;
    }

    /// <summary>
    /// Builds the <see cref="TokenValidationParameters"/> for the configured signing
    /// algorithm. RS256 deployments (the default) load the public key from
    /// <see cref="JwtSettings.RsaPublicKeyPem"/>; HS256 uses the Base64 HMAC secret. The validator pins
    /// <see cref="TokenValidationParameters.ValidAlgorithms"/> so an attacker cannot swap
    /// algorithms (e.g., signing an HS256 token with the RSA public key as the HMAC secret).
    /// </summary>
    internal static TokenValidationParameters BuildValidationParameters(JwtSettings jwtSettings)
    {
        if (jwtSettings.SigningAlgorithm == JwtSigningAlgorithm.RS256)
        {
            if (string.IsNullOrWhiteSpace(jwtSettings.RsaPublicKeyPem))
            {
                throw new InvalidOperationException(
                    "JwtSettings.RsaPublicKeyPem is required when SigningAlgorithm is RS256 and AddCommonAuthentication is used (in-process validation). For services that should fetch the public key via JWKS at runtime, use AddForwardedJwtBearer instead.");
            }

#pragma warning disable CA2000 // The RSA instance is captured by RsaSecurityKey which is held by JwtBearerOptions for the app lifetime.
            var validationRsa = RSA.Create();
#pragma warning restore CA2000
            validationRsa.ImportFromPem(jwtSettings.RsaPublicKeyPem);
            return new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwtSettings.Issuer,
                ValidAudience = jwtSettings.Audience,
                IssuerSigningKey = new RsaSecurityKey(validationRsa),
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            };
        }

        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidAudience = jwtSettings.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(
                GetValidatedSigningKey(
                    jwtSettings.SecretForKey
                    ?? throw new KeyNotFoundException("SecretForKey not found or invalid"))),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        };
    }

}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Aspire.Health;
using MMCA.Common.Aspire.Warmup;

namespace MMCA.Common.Aspire;

public static partial class Extensions
{
    extension<TBuilder>(TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        /// <summary>
        /// Adds a baseline "self" health check tagged with "live". The /alive endpoint filters
        /// to this tag for Kubernetes-style liveness probes, while /health requires all checks
        /// (including module and database checks added elsewhere) to pass.
        /// </summary>
        /// <returns>The same builder instance for chaining.</returns>
        public TBuilder AddDefaultHealthChecks()
        {
            builder.Services.AddHealthChecks()
                .AddCheck("self", () => HealthCheckResult.Healthy(), [HealthCheckTags.Live]);

            // SECURITY (SEC-Common-71, SEC-ADC-17): the short-TTL, single-flight report cache that
            // MapDefaultEndpoints serves /health and /health/ready from, so an anonymous flood on
            // either path cannot amplify into one probe round per request.
            builder.Services.AddOptions<HealthReportCacheOptions>()
                .Bind(builder.Configuration.GetSection(HealthReportCacheOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();
            builder.Services.TryAddSingleton(TimeProvider.System);
            builder.Services.TryAddSingleton<CachedHealthReportProvider>();

            return builder;
        }

        /// <summary>
        /// Conditionally registers health checks for infrastructure dependencies (SQL Server,
        /// PostgreSQL, SQLite,
        /// Redis, RabbitMQ) when their connection strings are configured. These are tagged as readiness
        /// checks only — they appear in <c>/health</c> but not <c>/alive</c>, so a transient
        /// infrastructure outage does not kill the process.
        /// </summary>
        /// <param name="requireDatabase">
        /// When <see langword="true"/>, the host must have SOME relational database configured
        /// (a <c>SQLServerConnectionString</c>, <c>PostgreSQLConnectionString</c> or
        /// <c>SqliteConnectionString</c>, at the top level or
        /// on a named <c>DataSources</c> entry) or startup throws.
        /// <para>
        /// This asymmetry with the Redis/RabbitMQ branches is DELIBERATE, not an oversight. Redis and
        /// RabbitMQ are optional per host, so their absence is a valid configuration. The service
        /// database is not: a host that cannot resolve its own connection string is misconfigured,
        /// and silently registering no check would let it report healthy and take traffic it cannot
        /// serve. The requirement is engine-agnostic on purpose, because an application picks its
        /// engine from configuration.
        /// </para>
        /// </param>
        /// <returns>The same builder instance for chaining.</returns>
        /// <exception cref="InvalidOperationException">
        /// <paramref name="requireDatabase"/> is <see langword="true"/> and no relational database is
        /// configured.
        /// </exception>
        public TBuilder AddInfrastructureHealthChecks(bool requireDatabase = false)
        {
            var healthChecks = builder.Services.AddHealthChecks();

            AddDatabaseHealthChecks(healthChecks, builder.Configuration, requireDatabase);

            var redisConnectionString = builder.Configuration.GetConnectionString("redis");
            if (!string.IsNullOrWhiteSpace(redisConnectionString))
            {
                // PING only, never an administrative command. The AspNetCore.HealthChecks.Redis check
                // this replaced issued CLUSTER INFO against any server it detected as clustered, which
                // is how StackExchange.Redis 3.x sees Azure Managed Redis (Enterprise tier), and the
                // server refuses that command outside admin mode: every probe threw against a healthy
                // cache. The check is registered as a singleton so the fallback multiplexer is built
                // once, not per probe, and disposed with the container.
                builder.Services.TryAddSingleton(
                    sp => new Health.RedisPingHealthCheck(redisConnectionString, sp));

                healthChecks.Add(new HealthCheckRegistration(
                    "redis",
                    sp => sp.GetRequiredService<Health.RedisPingHealthCheck>(),
                    failureStatus: null,
                    tags: [HealthCheckTags.Optional]));
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
                }, name: "rabbitmq", tags: [HealthCheckTags.Optional]);
            }

            return builder;
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
            // SECURITY: probes are declared anonymous explicitly. A host that adopts the
            // framework's fallback authorization policy would otherwise 401 its own liveness and
            // readiness probes, and a probe that cannot answer takes the replica out of rotation.
            //
            // SECURITY (SEC-Common-71, SEC-ADC-17): /health and /health/ready run the dependency
            // probes through a short-TTL single-flight cache, so an anonymous flood on either path
            // costs one probe round per window instead of one per request. The rate-limit bypass
            // these paths sit on cannot bound that amplification; the cache can. /alive below is
            // deliberately left uncached.
            app.MapCachedHealthChecks(HealthEndpointPaths.Health, predicate: null);

            // Liveness: only the "self" check (tagged "live") — avoids marking the
            // process as dead when an external dependency (e.g., SQL Server) is down.
            app.MapHealthChecks(HealthEndpointPaths.Alive, new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains(HealthCheckTags.Live)
            }).AllowAnonymous();

            // Readiness: everything except "live"-only and "optional" checks. Warm-up gate is
            // tagged "ready" and reports unhealthy until WarmupHostedService finishes; untagged
            // dependency checks (e.g., AddSqlServer) also show up here so a failing dependency
            // removes the replica from traffic without restarting the container.
            //
            // "optional" is excluded on purpose. A dependency the app degrades gracefully without
            // (a distributed cache behind an in-memory fallback, a broker behind a retrying outbox)
            // must NOT gate readiness: making it readiness-fatal converts a partial degradation
            // into a total outage, because every replica goes unready at once and the app stops
            // serving traffic it was perfectly capable of serving. Those checks still surface on
            // /health, so the degradation is visible without being self-inflicted.
            app.MapCachedHealthChecks(
                HealthEndpointPaths.Ready,
                static r => !r.Tags.Contains(HealthCheckTags.Live) && !r.Tags.Contains(HealthCheckTags.Optional));

            return app;
        }

        /// <summary>
        /// Maps one anonymous probe endpoint served from <see cref="CachedHealthReportProvider"/>,
        /// answering exactly as <c>MapHealthChecks</c> does (the status name as
        /// <c>text/plain</c>, <c>503</c> only when unhealthy) but running the dependency probes at
        /// most once per cache window (SEC-Common-71, SEC-ADC-17).
        /// </summary>
        /// <param name="path">The endpoint path, used as the cache key so two endpoints never share a report.</param>
        /// <param name="predicate">Which registered checks take part; <see langword="null"/> means all of them.</param>
        /// <returns>The endpoint builder, so a host can add its own metadata.</returns>
        public IEndpointConventionBuilder MapCachedHealthChecks(
            string path,
            Func<HealthCheckRegistration, bool>? predicate)
            => app.MapMethods(
                path,
                [HttpMethods.Get, HttpMethods.Head],
                async (HttpContext httpContext, CachedHealthReportProvider provider, CancellationToken cancellationToken) =>
                {
                    var report = await provider
                        .GetReportAsync(path, predicate, cancellationToken)
                        .ConfigureAwait(false);

                    httpContext.Response.StatusCode = report.Status == HealthStatus.Unhealthy
                        ? StatusCodes.Status503ServiceUnavailable
                        : StatusCodes.Status200OK;
                    httpContext.Response.ContentType = "text/plain";

                    await httpContext.Response
                        .WriteAsync(report.Status.ToString(), cancellationToken)
                        .ConfigureAwait(false);
                })
                .AllowAnonymous()
                .ExcludeFromDescription();
    }

    /// <summary>
    /// Registers the relational database checks and enforces the "the database must be there" rule
    /// when the caller asks for it. Every relational engine is checked because a host picks one from
    /// configuration: SQL Server or PostgreSQL for a deployed service, SQLite for a small
    /// single-node application.
    /// No check is tagged optional, so they all gate readiness (see the tagging note on the Redis
    /// branch for why that distinction matters). A host that owns several databases gets one check
    /// per database: readiness means every database it serves from is reachable.
    /// </summary>
    /// <param name="healthChecks">The builder the checks are added to.</param>
    /// <param name="configuration">Configuration carrying the connection strings.</param>
    /// <param name="requireDatabase">Fail when no relational engine at all is configured.</param>
    /// <exception cref="InvalidOperationException">A required connection string is missing.</exception>
    private static void AddDatabaseHealthChecks(
        IHealthChecksBuilder healthChecks,
        IConfiguration configuration,
        bool requireDatabase)
    {
        var sqlSources = RelationalSources(configuration, "SQLServerConnectionString");
        var postgresSources = RelationalSources(configuration, "PostgreSQLConnectionString");
        var sqliteSources = RelationalSources(configuration, "SqliteConnectionString");

        if (requireDatabase && sqlSources.Count == 0 && postgresSources.Count == 0 && sqliteSources.Count == 0)
        {
            throw new InvalidOperationException(
                "No database connection string is configured: set a SQLServerConnectionString, "
                + "PostgreSQLConnectionString or SqliteConnectionString, "
                + "either at the top level under ConnectionStrings or on a named DataSources entry.");
        }

        foreach (var (name, connectionString) in sqlSources)
        {
            healthChecks.AddSqlServer(connectionString, name: name);
        }

        foreach (var (name, connectionString) in postgresSources)
        {
            healthChecks.AddNpgSql(connectionString, name: name);
        }

        foreach (var (name, connectionString) in sqliteSources)
        {
            healthChecks.AddSqlite(connectionString, name: name);
        }
    }

    /// <summary>
    /// Every distinct database the host declares on one relational engine, as (check name,
    /// connection string) pairs. Both configuration shapes are read: the top-level
    /// <c>ConnectionStrings</c> section and every named <c>DataSources</c> entry, so a
    /// database-per-service host that declares its database only under <c>DataSources</c> is checked
    /// exactly like a single-database one.
    /// </summary>
    /// <param name="configuration">Configuration carrying the connection strings.</param>
    /// <param name="key">The engine's connection-string key, identical in both sections.</param>
    /// <returns>The databases to check, deduplicated by connection string.</returns>
    /// <remarks>
    /// The FIRST database keeps the historical check name for its engine (<c>sqlserver</c> /
    /// <c>postgresql</c> / <c>sqlite</c>), and only a host that genuinely has a second, different database gets the
    /// <c>{engine}:{source}</c> form. Deduplication is by connection string, so the entries that
    /// collapse onto one physical database (the resolver's single-database collapse) contribute one
    /// check, not one per logical name.
    /// </remarks>
    private static List<(string Name, string ConnectionString)> RelationalSources(IConfiguration configuration, string key)
    {
        var engineName = key switch
        {
            "SqliteConnectionString" => "sqlite",
            "PostgreSQLConnectionString" => "postgresql",
            _ => "sqlserver",
        };

        var declared = new List<(string Source, string ConnectionString)>();

        var topLevel = configuration.GetConnectionString(key);
        if (!string.IsNullOrWhiteSpace(topLevel))
        {
            declared.Add((engineName, topLevel));
        }

        foreach (var entry in configuration.GetSection("DataSources").GetChildren())
        {
            var connectionString = entry[key];
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                declared.Add(($"{engineName}:{entry.Key}", connectionString));
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sources = new List<(string Name, string ConnectionString)>();
        foreach (var (source, connectionString) in declared)
        {
            if (seen.Add(connectionString))
            {
                sources.Add((sources.Count == 0 ? engineName : source, connectionString));
            }
        }

        return sources;
    }
}

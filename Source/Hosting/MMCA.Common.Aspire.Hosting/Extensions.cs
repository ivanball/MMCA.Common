using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;

namespace MMCA.Common.Aspire.Hosting;

/// <summary>
/// AppHost-side extensions used by extracted-microservice deployments to wire the
/// cross-cutting infrastructure (broker, JWKS-based identity discovery, message-bus
/// configuration) onto Aspire project resources.
/// <para>
/// These extensions live in a separate assembly from <c>MMCA.Common.Aspire</c> (which is
/// the service-defaults assembly consumed by every running service) so that running
/// services do not pull in the full <c>Aspire.Hosting</c> package and its dependencies.
/// </para>
/// </summary>
public static class Extensions
{
    /// <summary>
    /// Default Aspire resource name used for the RabbitMQ container.
    /// </summary>
    public const string DefaultBrokerResourceName = "rabbitmq";

    /// <summary>
    /// Provisions a RabbitMQ container resource with the management plugin enabled and
    /// returns the resource builder for further wiring. Production deployments should
    /// override the connection string via configuration so the same projects can target
    /// Azure Service Bus without changing the AppHost.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The resource name (defaults to <see cref="DefaultBrokerResourceName"/>).</param>
    /// <returns>The RabbitMQ resource builder for chaining.</returns>
    public static IResourceBuilder<RabbitMQServerResource> AddMessageBroker(
        this IDistributedApplicationBuilder builder,
        string name = DefaultBrokerResourceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddRabbitMQ(name).WithManagementPlugin();
    }

    /// <summary>
    /// Wires the message-bus connection string and provider env var onto a project
    /// resource so the consuming service's <c>AddBrokerMessaging</c> picks up RabbitMQ
    /// as the active transport. Adds a wait-for so the project does not start until the
    /// broker container is healthy.
    /// </summary>
    /// <typeparam name="TResource">The project resource type.</typeparam>
    /// <param name="service">The project resource builder.</param>
    /// <param name="broker">The broker resource builder.</param>
    /// <returns>The project resource builder for chaining.</returns>
    public static IResourceBuilder<TResource> WithBroker<TResource>(
        this IResourceBuilder<TResource> service,
        IResourceBuilder<RabbitMQServerResource> broker)
        where TResource : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(broker);

        return service
            .WithReference(broker)
            .WaitFor(broker)
            .WithEnvironment("MessageBus__Provider", "RabbitMq");
    }

    /// <summary>
    /// Wires JWKS-based JWT authority discovery onto a project resource. The consuming
    /// service should call <c>AddForwardedJwtBearer(authority, audience)</c> using the
    /// <c>Authentication__JwtBearer__Authority</c> environment variable that this method sets,
    /// so its JWT bearer middleware fetches the Identity service's JWKS document and
    /// validates tokens against the published RSA public key.
    /// </summary>
    /// <typeparam name="TResource">The project resource type.</typeparam>
    /// <param name="service">The project resource builder.</param>
    /// <param name="identity">The Identity service project resource builder.</param>
    /// <param name="gateway">
    /// Optional gateway resource. When provided, the JwtBearer authority is set to the gateway's
    /// HTTPS endpoint (which supports HTTP/1.1 + HTTP/2 via ALPN and forwards /.well-known/* to
    /// Identity). When omitted, falls back to Identity's HTTPS endpoint (which is Http2-only
    /// and may reject default HttpClient HTTP/1.1 requests).
    /// </param>
    /// <returns>The project resource builder for chaining.</returns>
    public static IResourceBuilder<TResource> WithJwksDiscovery<TResource>(
        this IResourceBuilder<TResource> service,
        IResourceBuilder<ProjectResource> identity,
        IResourceBuilder<ProjectResource>? gateway = null)
        where TResource : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(identity);

        return service
            .WithReference(identity)
            .WaitFor(identity)
            .WithEnvironment(context =>
            {
                // Identity (and the other extracted services) listen Http2-only on cleartext so
                // cross-service gRPC clients can negotiate h2c prior knowledge. The downside is
                // that the default JwtBearer backchannel HttpClient sends HTTP/1.1, which Kestrel
                // rejects on Http2-only endpoints. Route the JwtBearer's metadata fetch through
                // the gateway instead: the gateway terminates TLS, supports both HTTP/1.1 and
                // HTTP/2 via ALPN, and (per the Gateway forwarder) routes /.well-known/* to
                // Identity over HTTP/2. So the default HTTP/1.1 backchannel works end-to-end.
                //
                // Fallback when no gateway is passed: use Identity's HTTPS endpoint. ALPN will
                // negotiate HTTP/2 with a default HttpClient — same caveat about HTTP/1.1
                // clients on Http2-only endpoints applies, so callers should prefer the gateway.
                var endpoint = gateway is not null
                    ? gateway.GetEndpoint("https")
                    : identity.GetEndpoint("https");
                context.EnvironmentVariables["Authentication__JwtBearer__Authority"] = endpoint;
            });
    }

    /// <summary>
    /// CI/E2E only: forwards an ephemeral RSA keypair from the AppHost's own environment to the
    /// Identity service so it can sign RS256 tokens. The signing keys are normally supplied via
    /// user-secrets (local) or Key Vault (prod); E2E workflows generate a throwaway keypair and pass
    /// it through the <c>E2E_JWT_PRIVATE_KEY_PEM</c> / <c>E2E_JWT_PUBLIC_KEY_PEM</c> environment
    /// variables. When both are present this maps them onto <c>Jwt__RsaPrivateKeyPem</c> /
    /// <c>Jwt__RsaPublicKeyPem</c> / <c>Jwks__RsaPublicKeyPem</c>; without the forwarding every CI
    /// login/register fails ("No supported key formats were found") and the readiness gate times
    /// out. No-op locally and in prod (vars absent, so user-secrets / Key Vault are used).
    /// </summary>
    /// <param name="identity">The Identity service project resource builder.</param>
    /// <returns>The Identity resource builder for chaining.</returns>
    public static IResourceBuilder<ProjectResource> WithE2eRsaKeys(this IResourceBuilder<ProjectResource> identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var privateKey = Environment.GetEnvironmentVariable("E2E_JWT_PRIVATE_KEY_PEM");
        var publicKey = Environment.GetEnvironmentVariable("E2E_JWT_PUBLIC_KEY_PEM");
        if (string.IsNullOrWhiteSpace(privateKey) || string.IsNullOrWhiteSpace(publicKey))
        {
            return identity;
        }

        return identity
            .WithEnvironment("Jwt__RsaPrivateKeyPem", privateKey)
            .WithEnvironment("Jwt__RsaPublicKeyPem", publicKey)
            .WithEnvironment("Jwks__RsaPublicKeyPem", publicKey);
    }

    /// <summary>
    /// Wires a service project to its own SQL Server database ("database per microservice", ADR-006).
    /// References the given database and injects its connection string twice:
    /// <list type="bullet">
    ///   <item><c>DataSources__{logicalName}__SQLServerConnectionString</c> — feeds the
    ///   MMCA.Common multi-database routing (entities whose logical source matches
    ///   <paramref name="logicalName"/> resolve to this database).</item>
    ///   <item><c>ConnectionStrings__SQLServerConnectionString</c> — keeps the framework's
    ///   <c>[Required]</c> validation and the <c>AddSqlServer</c> health checks working, and makes
    ///   the service's <c>Default</c> source point at its own database. Because both values are
    ///   identical, the resolver collapses the logical name onto Default — one context, one
    ///   change tracker, one migration set per service.</item>
    /// </list>
    /// </summary>
    /// <param name="service">The service project resource.</param>
    /// <param name="database">The service's own database resource.</param>
    /// <param name="logicalName">The module's logical data source name (e.g. <c>"Catalog"</c>).</param>
    /// <returns>The service resource builder for chaining.</returns>
    public static IResourceBuilder<ProjectResource> WithSQLServerDataSource(
        this IResourceBuilder<ProjectResource> service,
        IResourceBuilder<SqlServerDatabaseResource> database,
        string logicalName)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(database);

        return service
            .WithReference(database)
            .WaitFor(database)
            .WithEnvironment($"DataSources__{logicalName}__SQLServerConnectionString", database.Resource.ConnectionStringExpression)
            .WithEnvironment("ConnectionStrings__SQLServerConnectionString", database.Resource.ConnectionStringExpression);
    }

    /// <summary>
    /// Wires a service project to an Azure Cosmos DB database for a given logical data source
    /// (polyglot persistence — entities whose configuration inherits <c>EntityTypeConfigurationCosmos</c>
    /// and resolve to <paramref name="logicalName"/> are stored here). Injects:
    /// <list type="bullet">
    ///   <item><c>DataSources__{logicalName}__CosmosConnectionString</c> — the account connection
    ///   string the multi-database resolver hands to <c>CosmosDbContext.UseCosmos(...)</c>.</item>
    ///   <item><c>DataSources__{logicalName}__CosmosDatabaseName</c> — the Cosmos database name
    ///   (<c>UseCosmos</c> takes the database separately from the connection string).</item>
    ///   <item><c>ConnectionStrings__CosmosConnectionString</c> — the framework's <c>[Required]</c>
    ///   validation / <c>Default</c> Cosmos source fallback.</item>
    /// </list>
    /// Unlike SQL Server, a service typically uses Cosmos for ONE module alongside its SQL Server
    /// source, so this is layered on top of (not instead of) <see cref="WithSQLServerDataSource"/>.
    /// </summary>
    /// <param name="service">The service project resource.</param>
    /// <param name="database">The Cosmos database resource (from <c>AddAzureCosmosDB(...).AddCosmosDatabase(...)</c>).</param>
    /// <param name="logicalName">The module's logical data source name (e.g. <c>"Conference"</c>).</param>
    /// <returns>The service resource builder for chaining.</returns>
    public static IResourceBuilder<ProjectResource> WithCosmosDataSource(
        this IResourceBuilder<ProjectResource> service,
        IResourceBuilder<AzureCosmosDBDatabaseResource> database,
        string logicalName)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(database);

        return service
            .WithReference(database)
            .WaitFor(database)
            .WithEnvironment($"DataSources__{logicalName}__CosmosConnectionString", database.Resource.ConnectionStringExpression)
            .WithEnvironment($"DataSources__{logicalName}__CosmosDatabaseName", database.Resource.DatabaseName)
            .WithEnvironment("ConnectionStrings__CosmosConnectionString", database.Resource.ConnectionStringExpression);
    }

    /// <summary>
    /// Wires a service project to a SQLite database file for a given logical data source (polyglot
    /// persistence — entities whose configuration inherits <c>EntityTypeConfigurationSqlite</c> and
    /// resolve to <paramref name="logicalName"/> are stored here). SQLite has no Aspire container
    /// resource (it is an in-process file), so this only injects connection-string env vars:
    /// <list type="bullet">
    ///   <item><c>DataSources__{logicalName}__SqliteConnectionString</c> — <c>Data Source=&lt;path&gt;</c>
    ///   handed to <c>SqliteDbContext.UseSqlite(...)</c>.</item>
    ///   <item><c>ConnectionStrings__SqliteConnectionString</c> — the framework's <c>Default</c>
    ///   SQLite source fallback.</item>
    /// </list>
    /// </summary>
    /// <param name="service">The service project resource.</param>
    /// <param name="logicalName">The module's logical data source name (e.g. <c>"Conference"</c>).</param>
    /// <param name="filePath">The absolute path to the SQLite database file.</param>
    /// <returns>The service resource builder for chaining.</returns>
    public static IResourceBuilder<ProjectResource> WithSqliteDataSource(
        this IResourceBuilder<ProjectResource> service,
        string logicalName,
        string filePath)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var connectionString = $"Data Source={filePath}";

        return service
            .WithEnvironment($"DataSources__{logicalName}__SqliteConnectionString", connectionString)
            .WithEnvironment("ConnectionStrings__SqliteConnectionString", connectionString);
    }
}

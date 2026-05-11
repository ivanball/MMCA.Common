using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

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
}

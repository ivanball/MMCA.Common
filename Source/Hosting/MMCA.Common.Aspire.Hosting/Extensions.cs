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
    /// <returns>The project resource builder for chaining.</returns>
    public static IResourceBuilder<TResource> WithJwksDiscovery<TResource>(
        this IResourceBuilder<TResource> service,
        IResourceBuilder<ProjectResource> identity)
        where TResource : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(identity);

        return service
            .WithReference(identity)
            .WaitFor(identity)
            .WithEnvironment(context =>
            {
                // Use the HTTP endpoint of the identity service for JWKS discovery; this is the
                // same URL clients use to fetch /.well-known/openid-configuration.
                var endpoint = identity.GetEndpoint("http");
                context.EnvironmentVariables["Authentication__JwtBearer__Authority"] = endpoint;
            });
    }
}

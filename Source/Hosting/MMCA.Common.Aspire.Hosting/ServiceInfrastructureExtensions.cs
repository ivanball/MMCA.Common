using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace MMCA.Common.Aspire.Hosting;

/// <summary>
/// The per-service infrastructure chain every extracted service repeats in its AppHost (its own SQL
/// Server database, the cache, the broker, the waits, the startup probe and the external endpoints),
/// as one call.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with extension(T) blocks, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class ServiceInfrastructureExtensions
{
    extension(IResourceBuilder<ProjectResource> service)
    {
        /// <summary>
        /// Wires a service project to its infrastructure, in this order: its own database as
        /// <c>DataSources__{logicalName}__SQLServerConnectionString</c> (with a wait on it), a reference
        /// to the cache, the broker (through <paramref name="attachBroker"/>, the delegate
        /// <c>AddSelectedBroker</c> returns), a wait on the cache, a wait on the mail server, the
        /// startup probe <paramref name="profile"/> selects, and external HTTP endpoints. The resulting
        /// waits are database, broker, cache, mail.
        /// <para>
        /// The probe is always a LIVENESS probe (<c>/alive</c>), never readiness: a startup gate pointed
        /// at a readiness endpoint can deadlock the dependency graph (see
        /// <see cref="H2cHealthCheckExtensions.DefaultProbePath"/>).
        /// </para>
        /// <para>
        /// Only the cache, the database and the broker are referenced; no project resource is, so the
        /// project-to-project edge set of the AppHost is exactly what the host declares itself.
        /// Host-specific additions (seeding switches, cross-service references, E2E keys) chain after
        /// this call.
        /// </para>
        /// </summary>
        /// <param name="database">The service's own SQL Server database resource.</param>
        /// <param name="logicalName">The module's logical data source name (e.g. <c>"Catalog"</c>).</param>
        /// <param name="cache">The cache resource (for example the Redis resource).</param>
        /// <param name="mailServer">The mail server resource (for example the one <c>AddMailDev</c> returns).</param>
        /// <param name="attachBroker">Attaches the selected broker to the service.</param>
        /// <param name="profile">The cleartext endpoint profile, which picks the probe.</param>
        /// <param name="probeEndpointName">
        /// The endpoint to probe. For <see cref="ServiceEndpointProfile.Http2Only"/> a null value probes
        /// <see cref="H2cHealthCheckExtensions.DefaultEndpointName"/>; for
        /// <see cref="ServiceEndpointProfile.Mixed"/> a null value leaves the endpoint choice to Aspire.
        /// </param>
        /// <returns>The same service resource builder, for chaining.</returns>
        public IResourceBuilder<ProjectResource> WithMmcaServiceInfrastructure(
            IResourceBuilder<SqlServerDatabaseResource> database,
            string logicalName,
            IResourceBuilder<IResourceWithConnectionString> cache,
            IResourceBuilder<IResource> mailServer,
            Func<IResourceBuilder<ProjectResource>, IResourceBuilder<ProjectResource>> attachBroker,
            ServiceEndpointProfile profile,
            string? probeEndpointName = null)
        {
            ArgumentNullException.ThrowIfNull(service);
            ArgumentNullException.ThrowIfNull(database);
            ArgumentNullException.ThrowIfNull(cache);
            ArgumentNullException.ThrowIfNull(mailServer);
            ArgumentNullException.ThrowIfNull(attachBroker);
            ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);

            var wired = service
                .WithSQLServerDataSource(database, logicalName)
                .WithReference(cache)
                .WithSelectedBroker(attachBroker)
                .WaitFor(cache)
                .WaitFor(mailServer);

            wired = profile == ServiceEndpointProfile.Mixed
                ? wired.WithHttpHealthCheck(H2cHealthCheckExtensions.DefaultProbePath, endpointName: probeEndpointName)
                : wired.WithH2cHealthCheck(
                    H2cHealthCheckExtensions.DefaultProbePath,
                    probeEndpointName ?? H2cHealthCheckExtensions.DefaultEndpointName);

            return wired.WithExternalHttpEndpoints();
        }
    }
}

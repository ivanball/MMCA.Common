using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using MMCA.Common.API.Startup;
using MMCA.Common.API.Startup.Endpoints;
using MMCA.Common.Aspire;

namespace MMCA.Common.ServiceHost;

/// <summary>
/// The composed service-host pipeline: the start-up steps every extracted service host repeats after
/// <c>builder.Build()</c>, as one call. The host maps its own gRPC services, hubs and MCP endpoint after
/// it.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with an extension(T) block in a static class, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class ServiceHostApplicationExtensions
{
    extension(WebApplication app)
    {
        /// <summary>
        /// Checks that the host registered its OpenAPI document, then initializes the database (migrate
        /// and seed; skipped only for the Development design-time OpenAPI run), then maps the health
        /// endpoints, applies the shared middleware pipeline, maps the OpenAPI document and, in
        /// Development only, the gRPC reflection service. Registers the disposal of the bootstrap logger
        /// factory on <c>ApplicationStopped</c>.
        /// <para>
        /// The host must call <c>builder.Services.AddOpenApi()</c> in its own Program.cs: the OpenAPI
        /// XML-comment generator attaches the controller summaries from the host project's XML comments
        /// only at an <c>AddOpenApi</c> call compiled in that project, so <c>AddMmcaServiceHost</c> pins
        /// the version but does not register the document.
        /// </para>
        /// </summary>
        /// <param name="host">What <c>AddMmcaServiceHost</c> returned.</param>
        /// <param name="cancellationToken">Cancellation token for the database initialization.</param>
        /// <returns>A task that completes when the pipeline is in place.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="host"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// The host never called <c>AddOpenApi()</c> (checked before anything is initialized or mapped).
        /// </exception>
        public async Task UseMmcaServiceHostAsync(MmcaServiceHost host, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(app);
            ArgumentNullException.ThrowIfNull(host);

            // The host's own AddOpenApi() call is what carries its controller summaries; refuse to start
            // without it, before the database is touched or anything is mapped.
            ServiceHostOpenApi.EnsureHostRegisteredDocument(app.Services);

            // Schema first, so nothing is mapped against a database that is not migrated and seeded.
            await app.InitializeDatabaseUnlessDesignTimeAsync(host.ModuleHost, cancellationToken).ConfigureAwait(false);

            app.MapDefaultEndpoints();
            app.UseCommonMiddlewarePipeline();
            app.MapCommonOpenApi();

            if (app.Environment.IsDevelopment())
            {
                // A Development-only descriptor service for grpcurl and the Aspire dashboard, declared
                // anonymous for the fallback authorization policy (SEC-Common-16).
                app.MapGrpcReflectionService().AllowAnonymous();
            }

            // The module loader keeps the logger it was created with, so the factory lives until the
            // application stops.
            app.Lifetime.ApplicationStopped.Register(host.BootstrapLoggerFactory.Dispose);
        }
    }
}

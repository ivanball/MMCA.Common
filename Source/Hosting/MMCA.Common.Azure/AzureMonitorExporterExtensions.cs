using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Aspire.Integrations;

namespace MMCA.Common.Azure;

/// <summary>
/// Registration extension that adds the Azure Monitor (Application Insights) OpenTelemetry exporter
/// beside the vendor-neutral OTLP exporter <c>AddServiceDefaults()</c> configures.
/// </summary>
public static class AzureMonitorExporterExtensions
{
    /// <summary>
    /// The environment variable the Azure Monitor distro reads its connection string from, and the
    /// gate for <see cref="AddAzureMonitorExporter{TBuilder}(TBuilder)"/>.
    /// </summary>
    public const string ConnectionStringKey = "APPLICATIONINSIGHTS_CONNECTION_STRING";

    extension<TBuilder>(TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        /// <summary>
        /// Exports logs, metrics and traces to Azure Monitor when
        /// <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> is present, which the cloud deployment sets
        /// (e.g. Container Apps Bicep) so the telemetry flows to the workspace-based Application
        /// Insights resource. When absent or whitespace this method does nothing, so local
        /// development and tests take no Azure dependency at startup.
        /// <para>
        /// Call it AFTER <c>AddServiceDefaults()</c>: the outbox-poll and probe filter processors
        /// that service defaults register must run before this exporter's batch processor, or the
        /// spans they un-record are exported anyway. Both exporters can be active simultaneously;
        /// each ships its own copy.
        /// </para>
        /// </summary>
        /// <returns>The same builder instance for chaining.</returns>
        /// <exception cref="InvalidOperationException">
        /// <c>AddServiceDefaults()</c> (or <c>ConfigureOpenTelemetry()</c>) has not run yet. Enforced
        /// whether or not the connection string is set, so a misordered host fails on a developer
        /// machine rather than only in the deployment that sets it.
        /// </exception>
        public TBuilder AddAzureMonitorExporter()
        {
            if (!builder.Services.Any(descriptor =>
                    descriptor.ServiceType == typeof(HostIntegrationMarker)
                    && !descriptor.IsKeyedService
                    && descriptor.ImplementationInstance is HostIntegrationMarker marker
                    && string.Equals(marker.Name, HostIntegrationNames.ServiceDefaultsTelemetry, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "AddAzureMonitorExporter() must run after AddServiceDefaults() (or ConfigureOpenTelemetry()): the outbox-poll and probe filter processors service defaults register have to run before the Azure Monitor exporter's batch processor, or the spans they drop are exported anyway. Move the call below builder.AddServiceDefaults().");
            }

            // The receipt the core startup tripwire checks: APPLICATIONINSIGHTS_CONNECTION_STRING set
            // without this call fails the host instead of starting it with no telemetry export.
            builder.Services.AddSingleton(new HostIntegrationMarker(HostIntegrationNames.AzureMonitorExporter));

            if (string.IsNullOrWhiteSpace(builder.Configuration[ConnectionStringKey]))
            {
                return builder;
            }

            builder.Services.AddOpenTelemetry().UseAzureMonitor();

            return builder;
        }
    }
}

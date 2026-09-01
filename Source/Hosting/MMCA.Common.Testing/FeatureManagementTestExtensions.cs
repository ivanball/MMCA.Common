using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FeatureManagement;

namespace MMCA.Common.Testing;

/// <summary>
/// Test helpers for configuring feature flags in integration test fixtures.
/// </summary>
public static class FeatureManagementTestExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Adds feature management with the specified feature flags configured via in-memory settings.
        /// Call this in <c>ConfigureServices</c> of a test <c>WebApplicationFactory</c> to override
        /// feature flag values from <c>appsettings.json</c>.
        /// <para>
        /// The flags are LAYERED on top of the <see cref="IConfiguration"/> the host already
        /// registered, and the resulting root is registered in its place, so everything else the host
        /// configured (connection strings, authentication settings, the data-source section) still
        /// resolves. .NET DI hands a non-collection dependency the LAST registration, so building a
        /// flags-only root here would silently give every component constructed afterwards a
        /// configuration with nothing but <c>FeatureManagement</c> in it.
        /// </para>
        /// <para>
        /// The host's configuration is picked up only when it was registered as an instance, which is
        /// how <c>WebApplicationFactory</c> and the framework's own hosts register it. Behind a
        /// factory registration there is nothing to read without building the provider, so the flags
        /// stand alone, exactly as they did before.
        /// </para>
        /// </summary>
        /// <param name="features">A dictionary of feature flag names to their enabled/disabled state.</param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection ConfigureTestFeatureFlags(
            Dictionary<string, bool> features)
        {
            ArgumentNullException.ThrowIfNull(features);

            var existing = services
                .LastOrDefault(d => d.ServiceType == typeof(IConfiguration))?
                .ImplementationInstance as IConfiguration;

            var builder = new ConfigurationBuilder();
            if (existing is not null)
            {
                builder.AddConfiguration(existing);
            }

            // Added last, so these keys win over any FeatureManagement section the host configured.
            var config = builder
                .AddInMemoryCollection(
                    features.Select(kvp =>
                        new KeyValuePair<string, string?>(
                            $"FeatureManagement:{kvp.Key}", kvp.Value.ToString())))
                .Build();

            services.AddSingleton<IConfiguration>(config);
            services.AddFeatureManagement(config.GetSection("FeatureManagement"));

            return services;
        }
    }
}

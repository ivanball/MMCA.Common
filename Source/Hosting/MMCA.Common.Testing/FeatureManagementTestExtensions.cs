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
        /// </summary>
        /// <param name="features">A dictionary of feature flag names to their enabled/disabled state.</param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection ConfigureTestFeatureFlags(
            Dictionary<string, bool> features)
        {
            var config = new ConfigurationBuilder()
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

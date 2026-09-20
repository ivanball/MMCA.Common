using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.UI.Web.Hardening;

/// <summary>
/// Registration and configuration for the Blazor circuit ceiling, bound from the
/// <c>BlazorCircuitLimits</c> configuration section.
/// </summary>
/// <remarks>
/// The two halves are deliberately separate calls, because they attach to different builders: the
/// disconnected-circuit retention is <see cref="CircuitOptions"/> on
/// <c>AddInteractiveServerComponents</c>, while the ACTIVE-circuit ceiling is a
/// <see cref="CircuitHandler"/> singleton in the container. They read the same section so the two
/// numbers cannot drift apart.
/// </remarks>
public static class BlazorCircuitLimitExtensions
{
    /// <summary>
    /// Builds the <see cref="CircuitOptions"/> configuration callback that applies the bound
    /// disconnected-circuit retention. Tighter than the framework defaults (100 retained circuits)
    /// because a retained circuit holds the same state an active one does while serving nobody.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>A callback for <c>AddInteractiveServerComponents</c>.</returns>
    public static Action<CircuitOptions> RetentionFrom(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var limits = configuration.GetSection(BlazorCircuitLimitSettings.SectionName)
            .Get<BlazorCircuitLimitSettings>() ?? new BlazorCircuitLimitSettings();

        return options =>
        {
            options.DisconnectedCircuitMaxRetained = limits.DisconnectedCircuitMaxRetained;
            options.DisconnectedCircuitRetentionPeriod =
                TimeSpan.FromSeconds(limits.DisconnectedCircuitRetentionSeconds);
        };
    }

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the ceiling on concurrently ACTIVE circuits. Singleton so ONE count spans the
        /// replica: circuit handlers are resolved from each circuit's own scope, and a scoped
        /// registration would count to one and cap nothing.
        /// </summary>
        /// <returns>The same service collection for chaining.</returns>
        public IServiceCollection AddBoundedBlazorCircuits()
        {
            ArgumentNullException.ThrowIfNull(services);

            services.AddOptions<BlazorCircuitLimitSettings>()
                .BindConfiguration(BlazorCircuitLimitSettings.SectionName)
                .ValidateDataAnnotations()
                .ValidateOnStart();

            return services.AddSingleton<CircuitHandler, BoundedCircuitHandler>();
        }
    }
}

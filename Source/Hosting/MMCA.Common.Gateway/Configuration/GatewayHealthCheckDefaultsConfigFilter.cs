using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;

namespace MMCA.Common.Gateway.Configuration;

/// <summary>
/// Fills in destination health-check defaults for clusters that declare none, so the proxy actually
/// ejects a failing destination instead of continuing to balance onto it.
/// <para>
/// Additive only: a cluster that already states a <c>Passive</c> block keeps it verbatim, and the
/// same holds for <c>Active</c>. The filter never edits an operator's explicit choice, it only
/// supplies one where the configuration was silent, which is the state a config-driven gateway
/// ships in by default.
/// </para>
/// </summary>
/// <param name="options">The bound gateway settings.</param>
public sealed class GatewayHealthCheckDefaultsConfigFilter(IOptions<GatewaySettings> options) : IProxyConfigFilter
{
    private readonly GatewaySettings _settings = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Applies the health-check defaults to <paramref name="cluster"/>.</summary>
    /// <param name="cluster">The cluster as loaded from configuration.</param>
    /// <param name="cancel">The cancellation token.</param>
    /// <returns>The cluster to use.</returns>
    public ValueTask<ClusterConfig> ConfigureClusterAsync(ClusterConfig cluster, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        var existing = cluster.HealthCheck;
        var passive = existing?.Passive ?? BuildPassive();
        var active = existing?.Active ?? BuildActive();

        if (ReferenceEquals(passive, existing?.Passive) && ReferenceEquals(active, existing?.Active))
        {
            return ValueTask.FromResult(cluster);
        }

        return ValueTask.FromResult(cluster with
        {
            HealthCheck = new HealthCheckConfig
            {
                Passive = passive,
                Active = active,
                AvailableDestinationsPolicy = existing?.AvailableDestinationsPolicy,
            },
        });
    }

    /// <summary>Routes carry no health-check configuration; only clusters do.</summary>
    /// <param name="route">The route as loaded from configuration.</param>
    /// <param name="cluster">The cluster the route targets, when it resolves.</param>
    /// <param name="cancel">The cancellation token.</param>
    /// <returns>The route unchanged.</returns>
    public ValueTask<RouteConfig> ConfigureRouteAsync(RouteConfig route, ClusterConfig? cluster, CancellationToken cancel) =>
        ValueTask.FromResult(route);

    /// <summary>The default passive block, or <see langword="null"/> when passive defaults are off.</summary>
    /// <returns>The block to apply.</returns>
    private PassiveHealthCheckConfig? BuildPassive()
    {
        var defaults = _settings.HealthCheckDefaults.Passive;
        return defaults.Enabled
            ? new PassiveHealthCheckConfig
            {
                Enabled = true,
                Policy = defaults.Policy,
                ReactivationPeriod = defaults.ReactivationPeriod,
            }
            : null;
    }

    /// <summary>The default active block, or <see langword="null"/> when active defaults are off.</summary>
    /// <returns>The block to apply.</returns>
    private ActiveHealthCheckConfig? BuildActive()
    {
        var defaults = _settings.HealthCheckDefaults.Active;
        return defaults.Enabled
            ? new ActiveHealthCheckConfig
            {
                Enabled = true,
                Policy = defaults.Policy,
                Interval = defaults.Interval,
                Timeout = defaults.Timeout,
                Path = defaults.Path,
            }
            : null;
    }
}

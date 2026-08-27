using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Gateway.Configuration;
using MMCA.Common.Gateway.RateLimiting;
using MMCA.Common.Gateway.Transforms;

namespace MMCA.Common.Gateway;

/// <summary>
/// The single composition entry point for the MMCA gateway building blocks: shared cluster request
/// profiles with an h2c rollback switch, destination health-check defaults, route/cluster trace
/// headers, and the named per-route rate-limiter policies YARP routes reference by name.
/// <para>
/// It deliberately does NOT load the route table: <c>LoadFromConfig</c> (or any other
/// <c>IProxyConfigProvider</c>) stays the host's call, because which section owns the routes, and
/// whether they come from configuration at all, is a host decision. This adds behavior ON TOP of
/// whatever config source the host chose.
/// </para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with extension(T) blocks, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class GatewayReverseProxyExtensions
{
    extension(IReverseProxyBuilder builder)
    {
        /// <summary>
        /// Binds <see cref="GatewaySettings"/> from the <c>MmcaGateway</c> configuration section and
        /// wires every building block onto the proxy.
        /// </summary>
        /// <param name="configuration">The application configuration.</param>
        /// <returns>The same builder for chaining.</returns>
        /// <remarks>
        /// <para>
        /// The two config filters are independent: the profile filter owns <c>HttpRequest</c>, the
        /// health-check filter owns <c>HealthCheck</c>, and neither reads what the other wrote, so
        /// their relative order carries no meaning.
        /// </para>
        /// <para>
        /// This registers services; it maps nothing. The host still calls <c>MapReverseProxy()</c>,
        /// and <c>UseRateLimiter()</c> if it declared any named policies.
        /// </para>
        /// </remarks>
        public IReverseProxyBuilder AddMmcaGateway(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configuration);

            var section = configuration.GetSection(GatewaySettings.SectionName);

            builder.Services.AddOptions<GatewaySettings>()
                .Bind(section)
                .ValidateDataAnnotations()
                .ValidateOnStart();

            return Wire(builder, section.Get<GatewaySettings>() ?? new GatewaySettings());
        }

        /// <summary>
        /// Wires every building block onto the proxy from already-built settings, for a host that
        /// composes its gateway settings in code rather than in configuration.
        /// </summary>
        /// <param name="settings">The gateway settings.</param>
        /// <returns>The same builder for chaining.</returns>
        public IReverseProxyBuilder AddMmcaGateway(GatewaySettings settings)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(settings);

            // An explicit IOptions<> instance rather than a Configure<> callback: GatewaySettings is
            // init-only by design (the filters close over it for the process lifetime), so the value
            // has to be supplied whole rather than mutated into place.
            builder.Services.AddSingleton<IOptions<GatewaySettings>>(Options.Create(settings));

            return Wire(builder, settings);
        }
    }

    /// <summary>The registration body shared by both public overloads.</summary>
    /// <param name="builder">The reverse-proxy builder.</param>
    /// <param name="settings">The resolved settings, needed eagerly for the named policies.</param>
    /// <returns>The same builder for chaining.</returns>
    private static IReverseProxyBuilder Wire(IReverseProxyBuilder builder, GatewaySettings settings)
    {
        builder.Services.AddGatewayRoutePolicies(settings);

        return builder
            .AddConfigFilter<GatewayClusterProfileConfigFilter>()
            .AddConfigFilter<GatewayHealthCheckDefaultsConfigFilter>()
            .AddTransforms<GatewayTraceHeaderTransformProvider>();
    }
}

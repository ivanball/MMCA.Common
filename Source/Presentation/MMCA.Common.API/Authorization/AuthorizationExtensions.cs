using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Authorization.Fallback;
using MMCA.Common.Shared.Auth.Permissions;

namespace MMCA.Common.API.Authorization;

/// <summary>
/// Registers the application's authorization model: the permission-based authorization mechanism
/// (handler, on-demand policy provider, and registry).
/// </summary>
public static class AuthorizationExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the ASP.NET Core authorization services and wires the permission-based
        /// authorization mechanism used by <see cref="HasPermissionAttribute"/>. Permissions are the
        /// one authorization model: an endpoint states the capability it needs and the registry maps
        /// roles to capabilities, so no policy name has to be pre-registered per role.
        /// </summary>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddAuthorizationPolicies() =>
            services.AddAuthorizationPolicies(configureFallback: null);

        /// <summary>
        /// Registers the ASP.NET Core authorization services, the permission-based authorization
        /// mechanism, and the framework's fallback policy.
        /// <para>
        /// SECURITY: the fallback policy makes an endpoint that declares NO authorization metadata
        /// require an authenticated caller, so forgetting <c>[Authorize]</c> on a controller fails
        /// closed instead of publishing every inherited action anonymously. Deliberate anonymous
        /// endpoints declare themselves with <c>[AllowAnonymous]</c> or <c>.AllowAnonymous()</c>,
        /// which is also what the anonymous-endpoint fitness gate reads. Framework and static
        /// surfaces that are endpoint-routed but carry no metadata (Blazor's framework files and
        /// circuit, static asset conventions, health probes, well-known documents) are exempt by
        /// path prefix; see <see cref="FallbackAuthorizationOptions.DefaultExemptPathPrefixes"/>.
        /// </para>
        /// <para>
        /// The documented opt-out is
        /// <c>AddAuthorizationPolicies(options =&gt; options.Enabled = false)</c>, which restores the
        /// previous behavior exactly (an undecorated endpoint is anonymous).
        /// </para>
        /// <para>
        /// <b>Adopting this in an existing host.</b> Two surfaces need a decision before the fallback
        /// is switched on. A YARP gateway's proxied routes carry no authorization metadata unless the
        /// route config sets one, so every public route needs
        /// <c>"AuthorizationPolicy": "anonymous"</c> (or the host opts out here). And a routable
        /// Blazor page is gated by <c>AuthorizeRouteView</c>, which reads attributes and ignores this
        /// policy entirely, so a page still declares <c>[Authorize]</c> or <c>[AllowAnonymous]</c> for
        /// itself; the fallback only covers its server-rendered endpoint.
        /// </para>
        /// </summary>
        /// <param name="configureFallback">
        /// Optional callback to disable the fallback policy or extend its exempt path prefixes.
        /// </param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddAuthorizationPolicies(Action<FallbackAuthorizationOptions>? configureFallback)
        {
            services.AddAuthorization();

            // Permission-based authorization. The on-demand policy provider materializes "perm:*"
            // policies and the handler evaluates them against the permission registry. Registered
            // here so every host that wires authentication gets the mechanism for free; consumers
            // declare their role -> permission grants via AddPermissions(...).
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IAuthorizationHandler, PermissionAuthorizationHandler>());
            services.Replace(
                ServiceDescriptor.Transient<IAuthorizationPolicyProvider, PermissionPolicyProvider>());
            EnsurePermissionRegistry(services);

            services.AddOptions<FallbackAuthorizationOptions>();
            if (configureFallback is not null)
            {
                services.Configure(configureFallback);
            }

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IAuthorizationHandler, FallbackAuthorizationHandler>());

            // Applied through the options pipeline rather than inline, so the Enabled flag is read
            // after every AddAuthorizationPolicies / Configure call this host makes.
            services.AddOptions<AuthorizationOptions>()
                .Configure<IOptions<FallbackAuthorizationOptions>>(static (authorization, fallback) =>
                    authorization.FallbackPolicy = fallback.Value.Enabled
                        ? new AuthorizationPolicyBuilder()
                            .AddRequirements(new FallbackAuthorizationRequirement())
                            .Build()
                        : null);

            return services;
        }

        /// <summary>
        /// Declares role -> permission grants that back <see cref="HasPermissionAttribute"/>. Safe to
        /// call once per module: grants accumulate (and union) into a single registry, so each module
        /// contributes only the permissions it owns. Call before the host is built.
        /// </summary>
        /// <param name="configure">Callback that adds grants via <see cref="PermissionRegistryBuilder"/>.</param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddPermissions(Action<PermissionRegistryBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            var builder = EnsurePermissionRegistry(services);
            configure(builder);

            return services;
        }
    }

    // Ensures a single shared PermissionRegistryBuilder (and the IPermissionRegistry built from it)
    // are registered, returning the builder so callers can accumulate grants into it. The registry
    // is built lazily on first resolve, after all modules have contributed.
    private static PermissionRegistryBuilder EnsurePermissionRegistry(IServiceCollection services)
    {
        if (services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(PermissionRegistryBuilder))
                ?.ImplementationInstance is PermissionRegistryBuilder existing)
        {
            return existing;
        }

        var builder = new PermissionRegistryBuilder();
        services.AddSingleton(builder);
        services.AddSingleton(_ => builder.Build());

        // Both contracts forward to the ONE built instance, so what an administration screen
        // enumerates and what an authorization check answers from can never disagree. The concrete
        // registration above is what makes them the same object; asking the builder twice would
        // build two.
        services.AddSingleton<IPermissionRegistry>(sp => sp.GetRequiredService<PermissionRegistry>());
        services.AddSingleton<IPermissionCatalog>(sp => sp.GetRequiredService<PermissionRegistry>());

        return builder;
    }
}

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Authorization.Fallback;
using MMCA.Common.API.Authorization.MultiFactor;
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
        /// A request that matched no endpoint and names no web-root file is not gated either, so it
        /// reaches the 404 (and a host's not-found re-execute) instead of a sign-in challenge.
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

            // Role-level second factor (Authorization:MultiFactor:RequiredForRoles). Inert while the
            // list is empty: the bearer withholding and the handler return before any work and the
            // wrapped result handler delegates every call.
            AddMultiFactorRoleRequirement(services);

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

    // Registers the role-level second-factor handler, binds its settings from the host's
    // configuration, and wraps the registered authorization result handler so the refusal carries a
    // coded body. Idempotent: AddAuthorizationPolicies runs more than once in a Blazor web host.
    private static void AddMultiFactorRoleRequirement(IServiceCollection services)
    {
        var alreadyRegistered = services.Any(descriptor =>
            !descriptor.IsKeyedService
            && descriptor.ServiceType == typeof(IAuthorizationHandler)
            && descriptor.ImplementationType == typeof(MultiFactorRoleAuthorizationHandler));
        if (!alreadyRegistered)
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IAuthorizationHandler, MultiFactorRoleAuthorizationHandler>());

            // GetService, not GetRequiredService: a host with no IConfiguration binds nothing and the
            // rule stays off. Bound once, so the list is not appended to twice. No ValidateDataAnnotations
            // or ValidateOnStart: the settings carry one role list and no annotations, and every value,
            // the empty default included, is valid (empty means the rule is off).
            services.AddOptions<MultiFactorAuthorizationSettings>()
                .Configure<IServiceProvider>(static (settings, provider) =>
                    provider.GetService<IConfiguration>()?.GetSection(MultiFactorAuthorizationSettings.SectionName).Bind(settings));
        }

        // Withholds a listed role from a bearer principal without a second factor, on every JWT bearer
        // scheme, once however often this runs (TryAddEnumerable keys on the implementation type).
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPostConfigureOptions<JwtBearerOptions>, MultiFactorBearerWithholding>());

        WrapResultHandler(services);
    }

    // Replaces the last registered IAuthorizationMiddlewareResultHandler with the multi-factor
    // wrapper around it, keeping its lifetime. services.AddAuthorization() has already TryAdded the
    // framework default, so there is always one to wrap. A descriptor that is already the wrapper is
    // left alone.
    private static void WrapResultHandler(IServiceCollection services)
    {
        var index = -1;
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (!services[i].IsKeyedService && services[i].ServiceType == typeof(IAuthorizationMiddlewareResultHandler))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return;
        }

        var original = services[index];
        if (original.ImplementationFactory?.Target is ResultHandlerWrapper)
        {
            return;
        }

        var wrapper = new ResultHandlerWrapper(original);
        services[index] = ServiceDescriptor.Describe(
            typeof(IAuthorizationMiddlewareResultHandler),
            wrapper.Create,
            original.Lifetime);
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

    /// <summary>
    /// Builds the originally registered result handler the way its descriptor says (instance,
    /// factory or type) and wraps it in <see cref="MultiFactorRefusalResultHandler"/>. Its own type is
    /// also the marker that a descriptor has already been wrapped.
    /// </summary>
    /// <param name="original">The descriptor being replaced.</param>
    private sealed class ResultHandlerWrapper(ServiceDescriptor original)
    {
        public MultiFactorRefusalResultHandler Create(IServiceProvider provider)
        {
            var inner = original.ImplementationInstance as IAuthorizationMiddlewareResultHandler
                ?? original.ImplementationFactory?.Invoke(provider) as IAuthorizationMiddlewareResultHandler
                ?? (IAuthorizationMiddlewareResultHandler)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!);

            var logger = provider.GetService<ILogger<MultiFactorRefusalResultHandler>>()
                ?? NullLogger<MultiFactorRefusalResultHandler>.Instance;

            return new MultiFactorRefusalResultHandler(inner, logger);
        }
    }
}

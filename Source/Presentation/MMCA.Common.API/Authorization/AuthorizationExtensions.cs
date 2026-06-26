using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.API.Authorization;

/// <summary>
/// Registers the application's authorization model: the named role/authentication policies plus
/// the permission-based authorization mechanism (handler, on-demand policy provider, and registry).
/// </summary>
public static class AuthorizationExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers named authorization policies used by controllers via <c>[Authorize(Policy = ...)]</c>
        /// (defined in <see cref="AuthorizationPolicies"/>) and wires the permission-based
        /// authorization mechanism used by <see cref="HasPermissionAttribute"/>.
        /// </summary>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddAuthorizationPolicies()
        {
            services.AddAuthorizationBuilder()
                .AddPolicy(AuthorizationPolicies.RequireOrganizer, policy =>
                    policy.RequireRole(RoleNames.Organizer))
                .AddPolicy(AuthorizationPolicies.RequireAttendee, policy =>
                    policy.RequireRole(RoleNames.Attendee))
                .AddPolicy(AuthorizationPolicies.RequireAdmin, policy =>
                    policy.RequireRole(RoleNames.Admin))
                .AddPolicy(AuthorizationPolicies.RequireAuthenticated, policy =>
                    policy.RequireAuthenticatedUser());

            // Permission-based authorization. The on-demand policy provider materializes "perm:*"
            // policies and the handler evaluates them against the permission registry. Registered
            // here so every host that wires authentication gets the mechanism for free; consumers
            // declare their role -> permission grants via AddPermissions(...).
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IAuthorizationHandler, PermissionAuthorizationHandler>());
            services.Replace(
                ServiceDescriptor.Transient<IAuthorizationPolicyProvider, PermissionPolicyProvider>());
            EnsurePermissionRegistry(services);

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
        services.AddSingleton<IPermissionRegistry>(_ => builder.Build());

        return builder;
    }
}

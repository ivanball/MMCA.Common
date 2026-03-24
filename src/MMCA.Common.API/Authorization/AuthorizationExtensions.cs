using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.API.Authorization;

/// <summary>
/// Registers the application's role-based and authentication authorization policies.
/// </summary>
public static class AuthorizationExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers named authorization policies used by controllers via <c>[Authorize(Policy = ...)]</c>.
        /// Policy names are defined as constants in <see cref="AuthorizationPolicies"/>.
        /// </summary>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddAuthorizationPolicies()
        {
            services.AddAuthorizationBuilder()
                .AddPolicy(AuthorizationPolicies.RequireOrganizer, policy =>
                    policy.RequireRole("Organizer"))
                .AddPolicy(AuthorizationPolicies.RequireAttendee, policy =>
                    policy.RequireRole("Attendee"))
                .AddPolicy(AuthorizationPolicies.RequireAuthenticated, policy =>
                    policy.RequireAuthenticatedUser());

            return services;
        }
    }
}

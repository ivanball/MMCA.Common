using System.Security.Claims;

namespace MMCA.Common.Grpc.Authentication;

/// <summary>
/// Tells a service caller apart from a user caller on an endpoint that admits both
/// (<see cref="ServiceTokenDefaults.InternalCallerPolicy"/>).
/// </summary>
public static class ServiceCallerClaimsPrincipalExtensions
{
    extension(ClaimsPrincipal principal)
    {
        /// <summary>
        /// Returns <see langword="true"/> only when one of the principal's identities was
        /// authenticated by the <see cref="ServiceTokenDefaults.AuthenticationScheme"/> scheme and
        /// carries a non-empty <see cref="ServiceTokenDefaults.ServiceClaimType"/> claim. A user
        /// token never qualifies, so an edge can let a service through and compare a user caller
        /// to the subject it asked for.
        /// </summary>
        /// <returns><see langword="true"/> for an authenticated service caller; otherwise <see langword="false"/>.</returns>
        public bool IsServiceCaller()
        {
            ArgumentNullException.ThrowIfNull(principal);

            return principal.Identities.Any(identity =>
                identity.IsAuthenticated
                && string.Equals(identity.AuthenticationType, ServiceTokenDefaults.AuthenticationScheme, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(identity.FindFirst(ServiceTokenDefaults.ServiceClaimType)?.Value));
        }
    }
}

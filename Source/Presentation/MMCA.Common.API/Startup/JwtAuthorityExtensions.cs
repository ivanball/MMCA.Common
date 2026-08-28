using Microsoft.Extensions.Configuration;

namespace MMCA.Common.API.Startup;

/// <summary>
/// Reads the JWT/JWKS authority a service host validates tokens against, failing fast when the
/// AppHost never injected it.
/// <para>
/// Every extracted service that is not the token issuer resolves the same value immediately before
/// <c>AddForwardedJwtBearer</c>, and every one of them has to fail at startup rather than boot with
/// no authority: a host that silently starts without one answers every authenticated request with a
/// 401 that looks like a token problem instead of a wiring problem.
/// </para>
/// </summary>
public static class JwtAuthorityExtensions
{
    /// <summary>
    /// Configuration key carrying the JWT bearer authority, set by the AppHost's
    /// <c>WithJwksDiscovery(identityService)</c> (and by the deployment template in Azure).
    /// </summary>
    public const string JwtAuthorityConfigKey = "Authentication:JwtBearer:Authority";

    extension(IConfiguration configuration)
    {
        /// <summary>
        /// Returns the configured JWT bearer authority, or throws when
        /// <see cref="JwtAuthorityConfigKey"/> is absent.
        /// </summary>
        /// <returns>The authority base URL to hand to <c>AddForwardedJwtBearer</c>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// The authority is not configured, meaning nothing wired
        /// <c>WithJwksDiscovery(identityService)</c> in the AppHost (or its deployment equivalent).
        /// </exception>
        public string GetRequiredJwtAuthority()
        {
            ArgumentNullException.ThrowIfNull(configuration);

            return configuration[JwtAuthorityConfigKey]
                ?? throw new InvalidOperationException(
                    "Authentication:JwtBearer:Authority is not configured. " +
                    "Wire .WithJwksDiscovery(identityService) in the AppHost.");
        }
    }
}

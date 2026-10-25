using Amazon;
using Amazon.Runtime;

namespace MMCA.Common.Aws;

/// <summary>
/// Points an AWS SDK client configuration at its endpoint, shared by every client the adapter builds
/// from a configuration section, so a section's <c>Region</c> and <c>ServiceUrl</c> keys mean the same
/// thing for storage, secrets and anything added later.
/// </summary>
internal static class AwsClientEndpoint
{
    /// <summary>
    /// A <paramref name="serviceUrl"/> selects a compatible or emulated endpoint (LocalStack, MinIO,
    /// Cloudflare R2), and <paramref name="region"/> then becomes its signing region; without one, the
    /// region selects the AWS endpoint. The two are mutually exclusive on the SDK config (setting a
    /// region endpoint clears the service URL), hence the branch. Neither set leaves the client on the
    /// AWS SDK region fallback chain.
    /// </summary>
    /// <param name="config">The client configuration to point.</param>
    /// <param name="serviceUrl">The configured endpoint override, if any.</param>
    /// <param name="region">The configured region system name, if any.</param>
    internal static void Apply(ClientConfig config, Uri? serviceUrl, string? region)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (serviceUrl is { IsAbsoluteUri: true })
        {
            config.ServiceURL = serviceUrl.AbsoluteUri;
            if (!string.IsNullOrWhiteSpace(region))
            {
                config.AuthenticationRegion = region;
            }
        }
        else if (!string.IsNullOrWhiteSpace(region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        }
    }
}

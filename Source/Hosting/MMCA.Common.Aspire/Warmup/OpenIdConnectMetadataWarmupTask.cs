using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.Aspire.Warmup;

/// <summary>
/// Pre-fetches the OpenID Connect discovery document
/// (<c>{authority}/.well-known/openid-configuration</c>) over HTTP using the shared
/// <see cref="IHttpClientFactory"/>. Without this warm-up the fetch happens lazily on the
/// first authenticated request — on a CPU-throttled idle ACA replica that fetch can stretch
/// past the client timeout, which is the textbook cause of the "first request fails, second
/// succeeds" pattern on Container Apps Consumption plan.
/// </summary>
/// <remarks>
/// This warms the authority's own discovery-doc cache and the host-level DNS path, and opens a
/// connection in this task's own <see cref="IHttpClientFactory"/> client pool. The <c>JwtBearer</c>
/// middleware's <c>ConfigurationManager</c> caches discovery state separately and fetches through
/// its own default backchannel <see cref="HttpClient"/> (no <c>Backchannel</c> or
/// <c>BackchannelHttpHandler</c> is configured), so on the very first authenticated request it
/// still performs its own fetch over a connection of its own; what it gains is a warmed authority,
/// not a shared warm connection.
/// </remarks>
internal sealed partial class OpenIdConnectMetadataWarmupTask(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<OpenIdConnectMetadataWarmupTask> logger) : IWarmupTask
{
    public string Name => "OpenIdConnectMetadata";

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var authority = configuration["Authentication:JwtBearer:Authority"];
        if (string.IsNullOrWhiteSpace(authority))
        {
            return;
        }

        if (!Uri.TryCreate(
                authority.TrimEnd('/') + "/.well-known/openid-configuration",
                UriKind.Absolute,
                out var discoveryUri))
        {
            LogInvalidAuthority(logger, authority);
            return;
        }

        using var client = httpClientFactory.CreateClient(nameof(OpenIdConnectMetadataWarmupTask));
        using var response = await client.GetAsync(discoveryUri, cancellationToken).ConfigureAwait(false);

        LogFetched(logger, discoveryUri, (int)response.StatusCode);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "OIDC warm-up: Authentication:JwtBearer:Authority {Authority} is not a valid absolute URI.")]
    private static partial void LogInvalidAuthority(ILogger logger, string authority);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "OIDC warm-up: GET {DiscoveryUri} returned {StatusCode}.")]
    private static partial void LogFetched(ILogger logger, Uri discoveryUri, int statusCode);
}

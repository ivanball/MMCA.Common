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
/// This warms the underlying network path (DNS, TCP, TLS, <see cref="HttpClient"/> pool) and
/// the authority's own discovery-doc cache. The <c>JwtBearer</c> middleware's
/// <c>ConfigurationManager</c> caches discovery state separately, so on the very first
/// authenticated request it still performs its own fetch — but because the connection is now
/// warm, that fetch completes in single-digit milliseconds.
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

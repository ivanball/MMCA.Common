using Google.Apis.Auth.OAuth2;

namespace MMCA.Common.Gcp.Telemetry;

/// <summary>
/// The default <see cref="IGoogleCloudTelemetryTokenSource"/>: an OAuth2 access token from
/// Application Default Credentials. The credential is loaded on the first request, not at
/// registration, so a host builds its container without Google credentials. The credential caches its
/// token and refreshes it itself, so each call is an in-memory read until the token nears expiry.
/// </summary>
internal sealed class ApplicationDefaultTelemetryTokenSource : IGoogleCloudTelemetryTokenSource
{
    /// <summary>
    /// The OAuth2 scope requested. The Telemetry API also accepts the narrower <c>trace.append</c>,
    /// but that one does not cover the logs this exporter also writes; <c>cloud-platform</c> covers
    /// both, and the identity's IAM roles (<c>roles/telemetry.writer</c> on the project and
    /// <c>roles/serviceusage.serviceUsageConsumer</c> on the quota project) are what bound it.
    /// </summary>
    internal const string Scope = "https://www.googleapis.com/auth/cloud-platform";

    private ITokenAccess? _credential;

    /// <inheritdoc />
    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        // A race between two first callers loads the credential twice and keeps the last one, which is
        // harmless; a failed load is not cached, so the next refresh tries again.
        var credential = _credential;
        if (credential is null)
        {
            credential = (await GoogleCredential.GetApplicationDefaultAsync(cancellationToken).ConfigureAwait(false))
                .CreateScoped(Scope);
            _credential = credential;
        }

        return await credential.GetAccessTokenForRequestAsync(authUri: null, cancellationToken).ConfigureAwait(false);
    }
}

using System.Net;
using System.Net.Http.Headers;

namespace MMCA.Common.Gcp.Telemetry;

/// <summary>
/// The <see cref="DelegatingHandler"/> on the exporter's <c>HttpClientFactory</c>: every request
/// carries <c>Authorization: Bearer &lt;token&gt;</c> from <see cref="GoogleCloudTelemetryAccessToken"/>
/// and <c>x-goog-user-project</c>, the quota project the Telemetry API bills the request to.
/// Both send paths are covered because the OTLP/HTTP exporter uses the synchronous one.
/// </summary>
/// <param name="accessToken">The held token, read per request.</param>
/// <param name="quotaProject">The Google Cloud project sent as the quota project.</param>
internal sealed class GoogleCloudTelemetryAuthHandler(GoogleCloudTelemetryAccessToken accessToken, string quotaProject)
    : DelegatingHandler
{
    /// <summary>The header naming the quota project.</summary>
    internal const string QuotaProjectHeader = "x-goog-user-project";

    /// <inheritdoc />
    /// <exception cref="HttpRequestException">No access token has been read yet.</exception>
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Authorize(request);
        return base.Send(request, cancellationToken);
    }

    /// <inheritdoc />
    /// <exception cref="HttpRequestException">No access token has been read yet.</exception>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Authorize(request);
        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Stamps both headers, or refuses the request when there is no token yet: the OTLP exporter
    /// records the <see cref="HttpRequestException"/> as a failed export, and no request without a
    /// credential ever leaves the process.
    /// </summary>
    private void Authorize(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var token = accessToken.Current;
        if (string.IsNullOrEmpty(token))
        {
            throw new HttpRequestException(
                "No Google Cloud access token has been read yet (Application Default Credentials unavailable), so the export was not sent.",
                inner: null,
                HttpStatusCode.ServiceUnavailable);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Remove(QuotaProjectHeader);
        request.Headers.TryAddWithoutValidation(QuotaProjectHeader, quotaProject);
    }
}

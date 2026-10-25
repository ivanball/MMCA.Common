namespace MMCA.Common.Gcp.Telemetry;

/// <summary>
/// Supplies the OAuth2 access token the Google Cloud Telemetry API accepts. The default is
/// <see cref="ApplicationDefaultTelemetryTokenSource"/>; a test registers its own before calling
/// <c>AddGoogleCloudTelemetryExporter()</c>, which only adds the default when none is registered.
/// </summary>
internal interface IGoogleCloudTelemetryTokenSource
{
    /// <summary>Returns a current access token.</summary>
    /// <param name="cancellationToken">Cancels the credential load or the token request.</param>
    /// <returns>The access token sent as the bearer credential.</returns>
    Task<string> GetTokenAsync(CancellationToken cancellationToken);
}

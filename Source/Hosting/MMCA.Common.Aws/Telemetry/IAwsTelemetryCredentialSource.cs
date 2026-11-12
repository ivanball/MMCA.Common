using Amazon.Runtime;

namespace MMCA.Common.Aws.Telemetry;

/// <summary>Supplies the credentials the telemetry export requests are signed with.</summary>
internal interface IAwsTelemetryCredentialSource
{
    /// <summary>
    /// Returns the credentials, or <see langword="null"/> when none can be resolved. A source that
    /// cannot resolve credentials may also throw; the signing handler treats both as "no credentials"
    /// and sends nothing.
    /// </summary>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The credentials, or <see langword="null"/>.</returns>
    ValueTask<AWSCredentials?> GetAsync(CancellationToken cancellationToken);
}

using Amazon.Runtime;

namespace MMCA.Common.Aws.Telemetry;

/// <summary>Credentials fixed at registration, from <c>AwsTelemetry:AccessKeyId</c> and <c>AwsTelemetry:SecretAccessKey</c>.</summary>
/// <param name="credentials">The credentials every request is signed with.</param>
internal sealed class StaticAwsTelemetryCredentialSource(AWSCredentials credentials) : IAwsTelemetryCredentialSource
{
    /// <inheritdoc />
    public ValueTask<AWSCredentials?> GetAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<AWSCredentials?>(credentials);

    /// <inheritdoc />
    public AWSCredentials? Get(CancellationToken cancellationToken) => credentials;
}

using Amazon.Runtime;
using Amazon.Runtime.Credentials;

namespace MMCA.Common.Aws.Telemetry;

/// <summary>
/// The AWS default credential chain (an IAM role on ECS, EKS or EC2, the environment, the shared
/// profile), resolved lazily on the first export, never at registration. A successful resolution is
/// kept for the life of the source (the SDK credentials refresh themselves); a failed one is not, so
/// the next export tries again. Concurrent callers share one lookup.
/// </summary>
internal sealed class DefaultChainAwsTelemetryCredentialSource : IAwsTelemetryCredentialSource
{
    private readonly Lock _sync = new();

    private AWSCredentials? _resolved;

    private Task<AWSCredentials?>? _lookup;

    /// <inheritdoc />
    public ValueTask<AWSCredentials?> GetAsync(CancellationToken cancellationToken)
    {
        var resolved = Volatile.Read(ref _resolved);
        if (resolved is not null)
        {
            return ValueTask.FromResult<AWSCredentials?>(resolved);
        }

        Task<AWSCredentials?> lookup;
        lock (_sync)
        {
            // A finished lookup that left nothing resolved failed: start a fresh one.
            if (_lookup is null || _lookup.IsCompleted)
            {
                _lookup = ResolveAsync();
            }

            lookup = _lookup;
        }

        return new ValueTask<AWSCredentials?>(lookup.WaitAsync(cancellationToken));
    }

    private async Task<AWSCredentials?> ResolveAsync()
    {
        var credentials = await DefaultAWSCredentialsIdentityResolver
            .GetCredentialsAsync(clientConfig: null)
            .ConfigureAwait(false);
        Volatile.Write(ref _resolved, credentials);
        return credentials;
    }
}

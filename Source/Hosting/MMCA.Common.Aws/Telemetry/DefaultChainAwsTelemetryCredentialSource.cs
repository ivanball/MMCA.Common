using Amazon.Runtime;
using Amazon.Runtime.Credentials;

namespace MMCA.Common.Aws.Telemetry;

/// <summary>
/// The AWS default credential chain (an IAM role on ECS, EKS or EC2, the environment, the shared
/// profile), never resolved at registration: <see cref="AwsTelemetryCredentialWarmup"/> resolves it
/// once while the host starts, and an export that finds it unresolved resolves it itself. A
/// successful resolution is kept for the life of the source (the SDK credentials refresh themselves);
/// a failed one is not, so the next export tries again.
/// <para>
/// The asynchronous path shares one lookup between concurrent callers. The synchronous path calls the
/// SDK's synchronous resolver (<c>DefaultAWSCredentialsIdentityResolver.GetCredentials</c>, which the
/// SDK bounds at 35 seconds), serialized so the two exporters' threads do not both run it, and never
/// waits on a task.
/// </para>
/// </summary>
internal sealed class DefaultChainAwsTelemetryCredentialSource : IAwsTelemetryCredentialSource
{
    private readonly Func<AWSCredentials?> _resolve;

    private readonly Func<Task<AWSCredentials?>> _resolveAsync;

    private readonly Lock _sync = new();

    private readonly Lock _synchronousResolution = new();

    private AWSCredentials? _resolved;

    private Task<AWSCredentials?>? _lookup;

    /// <summary>Initializes a new instance of the <see cref="DefaultChainAwsTelemetryCredentialSource"/> class over the SDK's default chain.</summary>
    public DefaultChainAwsTelemetryCredentialSource()
        : this(
            static () => DefaultAWSCredentialsIdentityResolver.GetCredentials(clientConfig: null),
            static async () => await DefaultAWSCredentialsIdentityResolver.GetCredentialsAsync(clientConfig: null).ConfigureAwait(false))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="DefaultChainAwsTelemetryCredentialSource"/> class over two resolvers.</summary>
    /// <param name="resolve">The synchronous resolver.</param>
    /// <param name="resolveAsync">The asynchronous resolver.</param>
    internal DefaultChainAwsTelemetryCredentialSource(Func<AWSCredentials?> resolve, Func<Task<AWSCredentials?>> resolveAsync)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(resolveAsync);

        _resolve = resolve;
        _resolveAsync = resolveAsync;
    }

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

    /// <inheritdoc />
    public AWSCredentials? Get(CancellationToken cancellationToken)
    {
        var resolved = Volatile.Read(ref _resolved);
        if (resolved is not null)
        {
            return resolved;
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_synchronousResolution)
        {
            // The other exporter's thread may have resolved it while this one waited.
            resolved = Volatile.Read(ref _resolved);
            if (resolved is not null)
            {
                return resolved;
            }

            resolved = _resolve();
            if (resolved is not null)
            {
                Volatile.Write(ref _resolved, resolved);
            }

            return resolved;
        }
    }

    private async Task<AWSCredentials?> ResolveAsync()
    {
        var credentials = await _resolveAsync().ConfigureAwait(false);
        if (credentials is not null)
        {
            Volatile.Write(ref _resolved, credentials);
        }

        return credentials;
    }
}

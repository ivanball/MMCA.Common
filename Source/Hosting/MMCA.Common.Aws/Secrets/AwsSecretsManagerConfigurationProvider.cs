using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Microsoft.Extensions.Configuration;

namespace MMCA.Common.Aws.Secrets;

/// <summary>
/// Reads every Secrets Manager secret under one name prefix into configuration, mapping each name
/// the way the Key Vault provider does, so one secret-name convention serves both clouds: the prefix
/// is stripped and a double dash becomes the configuration separator, so
/// <c>adc/prod/ConnectionStrings--Default</c> arrives as <c>ConnectionStrings:Default</c>.
/// <para>
/// One load is a paged <c>ListSecrets</c> filtered by name, then <c>BatchGetSecretValue</c> over the
/// listed names in chunks of <see cref="BatchSize"/>. The service's name filter is a case-insensitive
/// prefix match, so every listed name is checked again ordinally and a secret named for another
/// environment in a different case stays out. Only string values are read; a binary-only secret is
/// skipped, since configuration holds strings.
/// </para>
/// <para>
/// The first load is synchronous, because <see cref="ConfigurationProvider.Load"/> is and the host
/// needs the values before the services that read them are registered; a secret that cannot be read
/// then fails startup, naming it. With a reload interval, later loads run on a timer, and a failed one
/// keeps the values already loaded (recorded in <see cref="LastReloadFailure"/>) rather than blanking
/// settings the host is using, the same posture as the Key Vault provider.
/// </para>
/// </summary>
internal sealed class AwsSecretsManagerConfigurationProvider : ConfigurationProvider, IDisposable
{
    /// <summary>The most secret ids one <c>BatchGetSecretValue</c> call accepts.</summary>
    internal const int BatchSize = 20;

    private const string KeyDelimiter = "--";

    private readonly IAmazonSecretsManager _client;
    private readonly string _namePrefix;
    private readonly TimeSpan? _reloadInterval;
    private readonly CancellationTokenSource _disposal = new();
    private Task? _polling;

    /// <summary>Initializes a new instance that owns <paramref name="client"/>.</summary>
    /// <param name="client">The Secrets Manager client; disposed with the provider.</param>
    /// <param name="namePrefix">The secret name prefix.</param>
    /// <param name="reloadInterval">The reload interval, or <see langword="null"/> to read once.</param>
    internal AwsSecretsManagerConfigurationProvider(IAmazonSecretsManager client, string namePrefix, TimeSpan? reloadInterval)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _namePrefix = namePrefix ?? throw new ArgumentNullException(nameof(namePrefix));
        _reloadInterval = reloadInterval;
    }

    /// <summary>Gets the failure of the most recent timed reload, or <see langword="null"/> when it succeeded.</summary>
    internal Exception? LastReloadFailure { get; private set; }

    /// <summary>
    /// Maps a secret name onto its configuration key: the prefix stripped, each <c>--</c> replaced by
    /// the configuration separator.
    /// </summary>
    /// <param name="secretName">The secret name.</param>
    /// <param name="namePrefix">The configured prefix.</param>
    /// <returns>The key, or <see langword="null"/> when the name is outside the prefix or is the prefix itself.</returns>
    internal static string? KeyFor(string secretName, string namePrefix)
    {
        if (!secretName.StartsWith(namePrefix, StringComparison.Ordinal) || secretName.Length == namePrefix.Length)
        {
            return null;
        }

        return secretName[namePrefix.Length..].Replace(KeyDelimiter, ConfigurationPath.KeyDelimiter, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override void Load()
    {
        // ConfigurationProvider.Load is synchronous and the AWS SDK v4 client is asynchronous only, so
        // the first read blocks, once, during host construction. Nothing above it holds a
        // synchronization context there (the configuration manager loads a source as it is added),
        // which is what makes the wait safe; the timed reloads below never block.
#pragma warning disable VSTHRD002 // Synchronous wait: Load is sync by contract and the SDK is async only.
        Data = LoadAsync(_disposal.Token).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002

        if (_reloadInterval is { } interval && _polling is null)
        {
            _polling = PollAsync(interval, _disposal.Token);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposal.Cancel();
        _disposal.Dispose();
        _client.Dispose();
    }

    private async Task PollAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await ReloadOnceAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disposed: the configuration root is going away, and the timer with it.
        }
    }

    private async Task ReloadOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var fresh = await LoadAsync(cancellationToken).ConfigureAwait(false);
            LastReloadFailure = null;
            if (!HasSameData(fresh))
            {
                Data = fresh;
                OnReload();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A throttled call, a network blip or a revoked grant must not take down a host that
            // already has its values: keep them and try again on the next tick.
            LastReloadFailure = ex;
        }
    }

    private async Task<Dictionary<string, string?>> LoadAsync(CancellationToken cancellationToken)
    {
        var names = await ListNamesAsync(cancellationToken).ConfigureAwait(false);
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var chunk in names.Chunk(BatchSize))
        {
            await ReadBatchAsync(chunk, data, cancellationToken).ConfigureAwait(false);
        }

        return data;
    }

    private async Task<List<string>> ListNamesAsync(CancellationToken cancellationToken)
    {
        List<string> names = [];
        string? nextToken = null;
        do
        {
            var response = await _client.ListSecretsAsync(
                new ListSecretsRequest
                {
                    Filters = [new Filter { Key = FilterNameStringType.Name, Values = [_namePrefix] }],
                    NextToken = nextToken,
                },
                cancellationToken).ConfigureAwait(false);

            foreach (var entry in response.SecretList ?? [])
            {
                if (entry.Name is { } name && KeyFor(name, _namePrefix) is not null)
                {
                    names.Add(name);
                }
            }

            nextToken = response.NextToken;
        }
        while (!string.IsNullOrEmpty(nextToken));

        return names;
    }

    private async Task ReadBatchAsync(string[] chunk, Dictionary<string, string?> data, CancellationToken cancellationToken)
    {
        string? nextToken = null;
        do
        {
            var response = await _client.BatchGetSecretValueAsync(
                new BatchGetSecretValueRequest { SecretIdList = [.. chunk], NextToken = nextToken },
                cancellationToken).ConfigureAwait(false);

            if (response.Errors is { Count: > 0 } errors)
            {
                throw new InvalidOperationException(
                    "Secrets Manager could not read "
                    + string.Join(", ", errors.Select(error => $"{error.SecretId} ({error.ErrorCode}: {error.Message})"))
                    + ". Grant the host identity secretsmanager:GetSecretValue (and kms:Decrypt on a customer-managed key) on every secret under the prefix, or move the secret out of it.");
            }

            foreach (var secret in response.SecretValues ?? [])
            {
                if (secret.Name is { } name && secret.SecretString is { } value && KeyFor(name, _namePrefix) is { } key)
                {
                    data[key] = value;
                }
            }

            nextToken = response.NextToken;
        }
        while (!string.IsNullOrEmpty(nextToken));
    }

    private bool HasSameData(Dictionary<string, string?> fresh) =>
        fresh.Count == Data.Count
        && fresh.All(pair => Data.TryGetValue(pair.Key, out var current) && string.Equals(current, pair.Value, StringComparison.Ordinal));
}

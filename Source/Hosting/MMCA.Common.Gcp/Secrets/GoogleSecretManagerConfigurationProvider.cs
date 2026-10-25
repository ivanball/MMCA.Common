using Google.Api.Gax.Grpc;
using Google.Api.Gax.ResourceNames;
using Google.Cloud.SecretManager.V1;
using Grpc.Core;
using Microsoft.Extensions.Configuration;

namespace MMCA.Common.Gcp.Secrets;

/// <summary>
/// Reads every Secret Manager secret under one id prefix into configuration, mapping each id the way
/// the Key Vault and Secrets Manager providers do, so one secret-name convention serves all three
/// clouds: the prefix is stripped and a double dash becomes the configuration separator, so
/// <c>adc-prod-ConnectionStrings--Default</c> arrives as <c>ConnectionStrings:Default</c>.
/// <para>
/// One load lists the project's secrets with a <c>name:"prefix"</c> filter, then, for each listed
/// secret, reads the state of its <c>latest</c> version and accesses that concrete version. The
/// service's name filter is a case-insensitive SUBSTRING match on the resource name, so every listed
/// id is checked again ordinally as a prefix, and a secret for another environment (a different
/// case, or an id that merely contains the prefix) stays out. A latest version that is disabled or
/// destroyed is skipped rather than read, since access to it fails by design; so is a secret with no
/// version yet. Payloads are read as UTF-8 text.
/// </para>
/// <para>
/// Every call uses the client's synchronous methods, so the first load needs no blocking wait on a
/// task: <see cref="ConfigurationProvider.Load"/> is synchronous and the host needs the values before
/// the services that read them are registered. A secret that cannot be read then fails startup,
/// naming it. With a reload interval, later loads run on a one-shot timer re-armed after each run (so
/// two loads never overlap), and a failed one keeps the values already loaded (recorded in
/// <see cref="LastReloadFailure"/>) rather than blanking settings the host is using, the same posture
/// as the Key Vault provider.
/// </para>
/// </summary>
internal sealed class GoogleSecretManagerConfigurationProvider : ConfigurationProvider, IDisposable
{
    private const string KeyDelimiter = "--";
    private const string LatestVersion = "latest";

    private readonly SecretManagerServiceClient _client;
    private readonly string _projectId;
    private readonly string _namePrefix;
    private readonly TimeSpan? _reloadInterval;
    private readonly CancellationTokenSource _disposal = new();
    private readonly Lock _timerGate = new();
    private Timer? _timer;
    private bool _disposed;

    /// <summary>Initializes a new instance over <paramref name="client"/>.</summary>
    /// <param name="client">The Secret Manager client.</param>
    /// <param name="projectId">The project whose secrets are read.</param>
    /// <param name="namePrefix">The secret id prefix.</param>
    /// <param name="reloadInterval">The reload interval, or <see langword="null"/> to read once.</param>
    internal GoogleSecretManagerConfigurationProvider(
        SecretManagerServiceClient client,
        string projectId,
        string namePrefix,
        TimeSpan? reloadInterval)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _projectId = projectId ?? throw new ArgumentNullException(nameof(projectId));
        _namePrefix = namePrefix ?? throw new ArgumentNullException(nameof(namePrefix));
        _reloadInterval = reloadInterval;
    }

    /// <summary>Gets the failure of the most recent timed reload, or <see langword="null"/> when it succeeded.</summary>
    internal Exception? LastReloadFailure { get; private set; }

    /// <summary>
    /// Maps a secret id onto its configuration key: the prefix stripped, each <c>--</c> replaced by
    /// the configuration separator.
    /// </summary>
    /// <param name="secretId">The secret id (the last segment of its resource name).</param>
    /// <param name="namePrefix">The configured prefix.</param>
    /// <returns>The key, or <see langword="null"/> when the id is outside the prefix or is the prefix itself.</returns>
    internal static string? KeyFor(string secretId, string namePrefix)
    {
        if (!secretId.StartsWith(namePrefix, StringComparison.Ordinal) || secretId.Length == namePrefix.Length)
        {
            return null;
        }

        return secretId[namePrefix.Length..].Replace(KeyDelimiter, ConfigurationPath.KeyDelimiter, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override void Load()
    {
        Data = Read();

        if (_reloadInterval is { } interval)
        {
            lock (_timerGate)
            {
                if (_timer is null && !_disposed)
                {
                    _timer = new Timer(_ => ReloadOnce(interval), null, interval, Timeout.InfiniteTimeSpan);
                }
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_timerGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer?.Dispose();
        }

        _disposal.Cancel();
        _disposal.Dispose();
    }

    private void ReloadOnce(TimeSpan interval)
    {
        try
        {
            var fresh = Read();
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
        finally
        {
            lock (_timerGate)
            {
                if (!_disposed)
                {
                    _timer?.Change(interval, Timeout.InfiniteTimeSpan);
                }
            }
        }
    }

    private Dictionary<string, string?> Read()
    {
        var callSettings = CallSettings.FromCancellationToken(_disposal.Token);
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var request = new ListSecretsRequest
        {
            ParentAsProjectName = ProjectName.FromProject(_projectId),
            Filter = $"name:\"{_namePrefix}\"",
        };

        List<string> secretIds = [];
        try
        {
            foreach (var secret in _client.ListSecrets(request, callSettings))
            {
                if (secret.SecretName?.SecretId is { } secretId && KeyFor(secretId, _namePrefix) is not null)
                {
                    secretIds.Add(secretId);
                }
            }
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            throw new InvalidOperationException(
                $"Secret Manager could not list the secrets of project {_projectId} ({ex.StatusCode}: {ex.Status.Detail}). Grant the host identity secretmanager.secrets.list on the project (roles/secretmanager.viewer).",
                ex);
        }

        foreach (var secretId in secretIds)
        {
            if (ReadLatest(secretId, callSettings) is { } value)
            {
                data[KeyFor(secretId, _namePrefix)!] = value;
            }
        }

        return data;
    }

    private string? ReadLatest(string secretId, CallSettings callSettings)
    {
        try
        {
            SecretVersion latest;
            try
            {
                latest = _client.GetSecretVersion(new SecretVersionName(_projectId, secretId, LatestVersion), callSettings);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                // No version yet (or the secret was deleted after the listing): nothing to read.
                return null;
            }

            if (latest.State != SecretVersion.Types.State.Enabled)
            {
                return null;
            }

            // The concrete version the state check saw, not the alias again, so a rotation landing
            // between the two calls cannot hand back a value whose state was never checked.
            return _client.AccessSecretVersion(latest.SecretVersionName, callSettings).Payload.Data.ToStringUtf8();
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            throw new InvalidOperationException(
                $"Secret Manager could not read {secretId} ({ex.StatusCode}: {ex.Status.Detail}). Grant the host identity secretmanager.versions.get and secretmanager.versions.access on every secret under the prefix (roles/secretmanager.viewer plus roles/secretmanager.secretAccessor), or move the secret out of it.",
                ex);
        }
    }

    private bool HasSameData(Dictionary<string, string?> fresh) =>
        fresh.Count == Data.Count
        && fresh.All(pair => Data.TryGetValue(pair.Key, out var current) && string.Equals(current, pair.Value, StringComparison.Ordinal));
}

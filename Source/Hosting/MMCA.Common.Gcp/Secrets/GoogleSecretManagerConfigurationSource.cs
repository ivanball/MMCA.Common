using Google.Cloud.SecretManager.V1;
using Microsoft.Extensions.Configuration;

namespace MMCA.Common.Gcp.Secrets;

/// <summary>
/// The configuration source <c>AddGoogleSecretManagerConfiguration</c> adds. It carries the settings
/// the call resolved and builds one <see cref="GoogleSecretManagerConfigurationProvider"/> over the
/// client the factory creates.
/// </summary>
/// <param name="clientFactory">Creates the Secret Manager client when the source is built.</param>
/// <param name="projectId">The project whose secrets are read.</param>
/// <param name="namePrefix">The secret id prefix; only secrets whose id starts with it are read.</param>
/// <param name="reloadInterval">How often to re-read the secrets, or <see langword="null"/> to read once.</param>
internal sealed class GoogleSecretManagerConfigurationSource(
    Func<SecretManagerServiceClient> clientFactory,
    string projectId,
    string namePrefix,
    TimeSpan? reloadInterval) : IConfigurationSource
{
    /// <summary>Gets the project whose secrets are read.</summary>
    internal string ProjectId { get; } = projectId;

    /// <summary>Gets the secret id prefix.</summary>
    internal string NamePrefix { get; } = namePrefix;

    /// <summary>Gets the reload interval, or <see langword="null"/> when the secrets are read once.</summary>
    internal TimeSpan? ReloadInterval { get; } = reloadInterval;

    /// <inheritdoc />
    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new GoogleSecretManagerConfigurationProvider(clientFactory(), ProjectId, NamePrefix, ReloadInterval);
}

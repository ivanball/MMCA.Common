using Amazon.SecretsManager;
using Microsoft.Extensions.Configuration;

namespace MMCA.Common.Aws.Secrets;

/// <summary>
/// The configuration source <c>AddAwsSecretsManagerConfiguration</c> adds. It carries the settings
/// the call resolved and builds one <see cref="AwsSecretsManagerConfigurationProvider"/>, which owns
/// the client it is given.
/// </summary>
/// <param name="clientFactory">Creates the Secrets Manager client when the source is built.</param>
/// <param name="namePrefix">The secret name prefix; only secrets whose name starts with it are read.</param>
/// <param name="reloadInterval">How often to re-read the secrets, or <see langword="null"/> to read once.</param>
internal sealed class AwsSecretsManagerConfigurationSource(
    Func<IAmazonSecretsManager> clientFactory,
    string namePrefix,
    TimeSpan? reloadInterval) : IConfigurationSource
{
    /// <summary>Gets the secret name prefix.</summary>
    internal string NamePrefix { get; } = namePrefix;

    /// <summary>Gets the reload interval, or <see langword="null"/> when the secrets are read once.</summary>
    internal TimeSpan? ReloadInterval { get; } = reloadInterval;

    /// <inheritdoc />
    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new AwsSecretsManagerConfigurationProvider(clientFactory(), NamePrefix, ReloadInterval);
}

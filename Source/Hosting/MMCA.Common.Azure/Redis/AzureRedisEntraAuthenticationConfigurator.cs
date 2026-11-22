using Azure.Core;
using MMCA.Common.Infrastructure.Caching;
using StackExchange.Redis;

namespace MMCA.Common.Azure.Redis;

/// <summary>
/// Puts every framework Redis connection on Microsoft Entra token login for Azure Cache for Redis and
/// Azure Managed Redis, through Microsoft.Azure.StackExchangeRedis: the user and password become the
/// identity's object id and an Entra access token, and the library re-authenticates each open
/// connection with a fresh token before the current one expires. The token is a bearer credential
/// sent as the password, so a connection that does not use TLS, or a connection string that still
/// carries an access key, is rejected rather than connected.
/// </summary>
/// <remarks>
/// <see cref="Configure"/> runs inside the options pipeline, which is synchronous, while the library's
/// configuration step is asynchronous (it acquires the first token before it returns). The wait runs
/// once per framework connection point, before that connection opens, and every connection point
/// shares one credential, so only the first acquires a token over the network.
/// </remarks>
internal sealed class AzureRedisEntraAuthenticationConfigurator : IRedisConfigurationOptionsConfigurator
{
    private readonly Func<ConfigurationOptions, Task> _configureForAzure;

    /// <summary>Initializes a new instance that signs in with <paramref name="credential"/>.</summary>
    /// <param name="credential">The credential the access tokens are requested from.</param>
    public AzureRedisEntraAuthenticationConfigurator(TokenCredential credential)
        : this(CreateConfigureStep(credential))
    {
    }

    /// <summary>Initializes a new instance over an explicit configuration step (tests).</summary>
    /// <param name="configureForAzure">Applies the Entra token login to the options.</param>
    internal AzureRedisEntraAuthenticationConfigurator(Func<ConfigurationOptions, Task> configureForAzure) =>
        _configureForAzure = configureForAzure ?? throw new ArgumentNullException(nameof(configureForAzure));

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// The connection string carries a password, or the connection does not use TLS.
    /// </exception>
    public void Configure(ConfigurationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrEmpty(options.Password))
        {
            throw new InvalidOperationException(
                $"{AzureRedisEntraAuthenticationSettings.SectionPath}:Enabled is true, but the Redis connection string for {Describe(options)} carries a password. "
                + "An explicit password takes precedence over the Microsoft Entra token, so the connection would keep signing in with the access key: remove the access key from the connection string.");
        }

#pragma warning disable VSTHRD002 // Synchronous wait: Configure is sync by contract (options pipeline) and the library step is async only.
        _configureForAzure(options).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002

        if (!options.Ssl)
        {
            throw new InvalidOperationException(
                $"{AzureRedisEntraAuthenticationSettings.SectionPath}:Enabled is true, but the Redis connection string for {Describe(options)} sets ssl=false. "
                + "A Microsoft Entra access token is sent as the password, so the connection must use TLS: remove 'ssl=false' (or set 'ssl=true').");
        }
    }

    private static Func<ConfigurationOptions, Task> CreateConfigureStep(TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return options => options.ConfigureForAzureWithTokenCredentialAsync(credential);
    }

    private static string Describe(ConfigurationOptions options) =>
        options.EndPoints.Count == 0
            ? "(no endpoint)"
            : string.Join(", ", options.EndPoints.Select(EndPointCollection.ToString));
}

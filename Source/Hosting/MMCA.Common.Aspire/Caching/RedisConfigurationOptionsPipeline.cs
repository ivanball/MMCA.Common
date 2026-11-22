using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MMCA.Common.Aspire.Caching;

/// <summary>
/// Builds the StackExchange.Redis <see cref="ConfigurationOptions"/> for a framework Redis connection
/// this package opens outside Aspire's Redis client (the output-cache store and the PING health
/// check's own connection), running the same post-configure steps Aspire's client options go through.
/// <para>
/// This is how those connections reach a Redis configurator (MMCA.Common.Infrastructure's
/// <c>IRedisConfigurationOptionsConfigurator</c>, for example the Entra token configurator of
/// MMCA.Common.Azure) without this package referencing Infrastructure: the configurator registration
/// adds an <see cref="IPostConfigureOptions{TOptions}"/> of <see cref="ConfigurationOptions"/>, and
/// both sides name only that StackExchange.Redis type and the options abstractions.
/// </para>
/// </summary>
internal static class RedisConfigurationOptionsPipeline
{
    /// <summary>
    /// The options for <paramref name="connectionString"/> with every registered post-configure step
    /// applied, or <see langword="null"/> when none is registered, in which case the caller keeps its
    /// connection-string path unchanged.
    /// </summary>
    /// <param name="services">The provider holding the post-configure steps.</param>
    /// <param name="connectionString">The Redis connection string.</param>
    /// <returns>The configured options, or <see langword="null"/>.</returns>
    internal static ConfigurationOptions? CreateConfigured(IServiceProvider services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        var postConfigures = services.GetServices<IPostConfigureOptions<ConfigurationOptions>>().ToArray();
        if (postConfigures.Length == 0)
        {
            return null;
        }

        var options = ConfigurationOptions.Parse(connectionString);
        foreach (var postConfigure in postConfigures)
        {
            postConfigure.PostConfigure(Options.DefaultName, options);
        }

        return options;
    }
}

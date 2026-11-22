using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MMCA.Common.Infrastructure.Caching;

/// <summary>
/// The options-pipeline bridge behind <see cref="IRedisConfigurationOptionsConfigurator"/>: runs every
/// registered configurator on each <see cref="ConfigurationOptions"/> instance the options system
/// builds (every name), after the instance has been parsed and bound. Aspire's Redis client reads its
/// options from this pipeline, and MMCA.Common.Aspire applies the same post-configure steps to the
/// options it parses for the output cache and the health check, so every framework connection runs the
/// configurators without that package naming this one.
/// </summary>
/// <param name="configurators">The registered configurators.</param>
internal sealed class RedisConfigurationOptionsPostConfigure(IEnumerable<IRedisConfigurationOptionsConfigurator> configurators)
    : IPostConfigureOptions<ConfigurationOptions>
{
    private readonly IRedisConfigurationOptionsConfigurator[] _configurators = [.. configurators];

    /// <inheritdoc />
    public void PostConfigure(string? name, ConfigurationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        foreach (var configurator in _configurators)
        {
            configurator.Configure(options);
        }
    }
}

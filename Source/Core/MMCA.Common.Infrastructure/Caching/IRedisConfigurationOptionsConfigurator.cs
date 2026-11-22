using StackExchange.Redis;

namespace MMCA.Common.Infrastructure.Caching;

/// <summary>
/// Configures the StackExchange.Redis <see cref="ConfigurationOptions"/> of every Redis connection the
/// framework opens, before it connects: a token credential, a client certificate, a timeout. Register
/// one with <c>services.AddRedisConfigurationOptionsConfigurator(...)</c>; an adapter package does
/// that from its own registration call (for example <c>AddAzureRedisEntraAuthentication(configuration)</c>).
/// <para>
/// The connections it reaches: the Redis client and the distributed cache over it
/// (<c>AddRedisCaching()</c>), the output-cache store (<c>AddRedisOutputCaching()</c>), the SignalR
/// backplane (<c>AddPushNotifications</c>) and the <c>redis</c> PING health check when it opens a
/// connection of its own. Each runs every registered configurator once on its own options instance,
/// parsed from the connection string. MMCA.Common.Aspire reaches the configurators without
/// referencing this package through the options pipeline: the registration call adds one
/// <c>IPostConfigureOptions&lt;ConfigurationOptions&gt;</c> that runs them, and both sides name only a
/// StackExchange.Redis type and the options abstractions.
/// </para>
/// <para>
/// With none registered nothing changes: every connection is made from the connection string exactly
/// as before.
/// </para>
/// </summary>
/// <remarks>
/// <see cref="Configure"/> runs synchronously inside the options pipeline, ahead of the connect, and
/// may run more than once per process (once per connection point). Throwing from it fails that
/// connection, which is the place to reject a connection string a configurator cannot serve.
/// </remarks>
public interface IRedisConfigurationOptionsConfigurator
{
    /// <summary>Applies this configurator's settings to the options of one Redis connection.</summary>
    /// <param name="options">The options, already parsed from the connection string.</param>
    void Configure(ConfigurationOptions options);
}

using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace MMCA.Common.Infrastructure.Configuration;

/// <summary>
/// The one name every piece of SHARED infrastructure is namespaced by: Redis cache keys,
/// distributed-lock keys, the SignalR Redis backplane channel prefix and the message broker's
/// endpoint (queue) prefix.
/// </summary>
/// <remarks>
/// <para>
/// SEC-Common-53. Two MMCA hosts pointed at one Redis or one broker used to share every keyspace by
/// default: the cache prefix defaulted to empty, the lock keys reused it, the broker endpoint prefix
/// was optional, and the SignalR backplane had no prefix knob at all. Because
/// <c>NotificationHub</c> ships in the framework, its backplane channel name is IDENTICAL in every
/// consumer, so a user-targeted push or a live-channel event published in one application was
/// delivered to the other application's clients holding the same numeric user id. Nothing warned.
/// </para>
/// <para>
/// The default is now the host's application name, which is distinct per deployed app and needs no
/// configuration. Set <c>Application:Namespace</c> to override it: use that when two hosts of the
/// SAME application must share a keyspace (a web head and a worker draining one backplane), and
/// keep it different everywhere else.
/// </para>
/// </remarks>
public static partial class ApplicationNamespace
{
    /// <summary>Configuration key carrying the explicit namespace.</summary>
    public const string ConfigurationKey = "Application:Namespace";

    /// <summary>Used when neither configuration nor the host environment names the application.</summary>
    public const string Fallback = "app";

    /// <summary>
    /// The key the generic host writes the application name to (<c>WebHostDefaults.ApplicationNameKey</c>),
    /// read when no <see cref="IHostEnvironment"/> is at hand.
    /// </summary>
    private const string HostApplicationNameKey = "applicationName";

    [GeneratedRegex("[^a-zA-Z0-9_-]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UnsafeCharacters { get; }

    /// <summary>
    /// Resolves the namespace: the explicit <see cref="ConfigurationKey"/> when set, otherwise the
    /// host's application name, otherwise <see cref="Fallback"/>. Never empty, so isolation is
    /// automatic rather than opt-in.
    /// </summary>
    /// <param name="configuration">Application configuration, or null.</param>
    /// <param name="environment">The host environment, or null.</param>
    /// <returns>A non-empty namespace safe to embed in a Redis key, a channel name and a queue name.</returns>
    public static string Resolve(IConfiguration? configuration, IHostEnvironment? environment)
    {
        var configured = configuration?[ConfigurationKey];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Sanitize(configured);
        }

        var applicationName = environment?.ApplicationName;
        if (string.IsNullOrWhiteSpace(applicationName))
        {
            applicationName = configuration?[HostApplicationNameKey];
        }

        return string.IsNullOrWhiteSpace(applicationName) ? Fallback : Sanitize(applicationName);
    }

    /// <summary>
    /// Reduces a name to characters every one of the four consumers accepts. A Redis key tolerates
    /// almost anything, a MassTransit queue name and a SignalR channel prefix do not, and the whole
    /// point is one value shared by all four.
    /// </summary>
    /// <param name="name">The raw name.</param>
    /// <returns>The sanitized name, or <see cref="Fallback"/> when nothing survives.</returns>
    public static string Sanitize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var cleaned = UnsafeCharacters.Replace(name, "-").Trim('-');
        return cleaned.Length == 0 ? Fallback : cleaned;
    }
}

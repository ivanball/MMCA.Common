using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;

namespace MMCA.Common.Infrastructure.Messaging.Broker;

/// <summary>
/// The one place broker entity names are derived, so a publisher and a consumer in different hosts
/// always agree. Every name is lower kebab case: runs of characters outside <c>[a-z0-9]</c> become a
/// single <c>-</c>, with none leading or trailing, which every supported broker accepts.
/// </summary>
public static class BrokerNaming
{
    /// <summary>The suffix trimmed from a host application name when deriving its queue name.</summary>
    private const string ServiceSuffix = "-service";

    /// <summary>
    /// The wire name of an integration event: its <c>[EventName]</c> declared name when it has one (stable
    /// across namespace moves), otherwise its CLR full name, so two events with the same short name in
    /// different modules never share a topic.
    /// </summary>
    /// <param name="eventType">The integration event type.</param>
    /// <returns>The kebab-case wire name.</returns>
    public static string EventName(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);

        return Kebab(EventNameResolver.GetDeclaredName(eventType) ?? eventType.FullName ?? eventType.Name);
    }

    /// <summary>The topic an event is published on: <c>{applicationNamespace}-{eventName}</c>.</summary>
    /// <param name="applicationNamespace">The resolved application namespace.</param>
    /// <param name="eventName">The event's wire name.</param>
    /// <returns>The topic name.</returns>
    public static string Topic(string applicationNamespace, string eventName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationNamespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        return Kebab(applicationNamespace) + "-" + eventName;
    }

    /// <summary>
    /// The host's queue name: the configured value when set, otherwise the host application name in kebab
    /// case with a trailing <c>-service</c> removed (<c>MMCA.ADC.Engagement.Service</c> becomes
    /// <c>mmca-adc-engagement</c>).
    /// </summary>
    /// <param name="configuredQueueName"><c>MessageBus:QueueName</c>, or <see langword="null"/>.</param>
    /// <param name="applicationName">The host application name.</param>
    /// <returns>The queue name.</returns>
    /// <exception cref="InvalidOperationException">Neither value yields a usable name.</exception>
    public static string Queue(string? configuredQueueName, string? applicationName)
    {
        var source = string.IsNullOrWhiteSpace(configuredQueueName) ? applicationName : configuredQueueName;
        var name = string.IsNullOrWhiteSpace(source) ? string.Empty : Kebab(source);

        if (string.IsNullOrWhiteSpace(configuredQueueName)
            && name.EndsWith(ServiceSuffix, StringComparison.Ordinal)
            && name.Length > ServiceSuffix.Length)
        {
            name = name[..^ServiceSuffix.Length];
        }

        return name.Length > 0
            ? name
            : throw new InvalidOperationException(
                "No broker queue name could be derived: set MessageBus:QueueName, or run the host with an application name.");
    }

    /// <summary>
    /// Fits <paramref name="name"/> into a broker's length limit deterministically: a name within the limit
    /// is returned unchanged; a longer one keeps its leading characters and ends with <c>-</c> plus the
    /// first eight hex digits of its SHA-256, so two long names stay distinct and every host computes the
    /// same result.
    /// </summary>
    /// <param name="name">The full name.</param>
    /// <param name="maxLength">The broker's limit; at least 16.</param>
    /// <returns>The name, shortened only when needed.</returns>
    public static string Fit(string name, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 16);

        if (name.Length <= maxLength)
        {
            return name;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8];
        return name[..(maxLength - hash.Length - 1)].TrimEnd('-') + "-" + hash;
    }

    /// <summary>Lower kebab case: runs outside <c>[a-z0-9]</c> collapse to one <c>-</c>, trimmed at both ends.</summary>
    /// <param name="value">The value to convert.</param>
    /// <returns>The kebab-case value.</returns>
    public static string Kebab(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var builder = new StringBuilder(value.Length + 8);
        var pendingSeparator = false;

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsAsciiLetterOrDigit(c))
            {
                // A lower-to-upper boundary inside a word (UserRegistered) is a word break too.
                if (char.IsAsciiLetterUpper(c) && i > 0 && char.IsAsciiLetterLower(value[i - 1]))
                {
                    pendingSeparator = true;
                }

                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append('-');
                }

                pendingSeparator = false;
                builder.Append(char.ToLower(c, CultureInfo.InvariantCulture));
            }
            else
            {
                pendingSeparator = true;
            }
        }

        return builder.ToString();
    }
}
